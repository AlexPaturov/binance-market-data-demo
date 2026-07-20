using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// CVD-дельта считается агрегацией в том же проходе по тикам, что и свеча (миграция 012);
/// CVD по окну — оконная сумма дельт по свечам, тики не перечитываются. При NULL-дельте
/// в окне (свеча до миграции или без тиковых данных) расчёт откатывается на тики —
/// оба пути обязаны давать одинаковый результат.
/// </summary>
public sealed class CvdPipelineTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _tradeRepository = null!;
    private AnalysisRepository _analysisRepository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;   // 2026-01-01T00:00:00Z

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _connectionString = _db.GetConnectionString();

        var schemaSql = await File.ReadAllTextAsync("02_baseline.sql");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(schemaSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString
            })
            .Build();

        _tradeRepository = new TradeRepository(configuration, NullLogger<TradeRepository>.Instance);
        _analysisRepository = new AnalysisRepository(configuration);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>
    /// Дельта минуты = покупки минус продажи. IsBuyerMaker = false — агрессор
    /// покупатель, объём в плюс.
    /// </summary>
    [Fact]
    public async Task Aggregation_ComputesCvdDelta_FromTheSameTickPass()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 2.0m, false),   // покупка: +2.0
            (2, Minute(0) + 2_000, 101m, 0.5m, true));   // продажа: −0.5

        await _tradeRepository.AggregateDirtyMinutesAsync(100);

        var delta = await QuerySingleAsync<decimal?>(
            @"SELECT ""CvdDelta"" FROM public.""Ohlcv_1min"" WHERE ""OpenTime"" = @T;",
            new { T = Minute(0) });

        Assert.Equal(1.5m, delta);
    }

    /// <summary>CVD по окну — накопленная сумма дельт по минутам.</summary>
    [Fact]
    public async Task GetCvd_FromCandles_AccumulatesDeltasAcrossMinutes()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 2.0m, false),   // минута 0: +2.0
            (2, Minute(0) + 2_000, 101m, 0.5m, true),    // минута 0: −0.5 → дельта +1.5
            (3, Minute(1) + 1_000, 102m, 0.7m, true));   // минута 1: −0.7 → CVD 0.8

        await _tradeRepository.AggregateDirtyMinutesAsync(100);

        var cvd = await GetCvdAsync();

        Assert.Equal(2, cvd.Count);
        Assert.Equal(1.5m, cvd[Minute(0)]);
        Assert.Equal(0.8m, cvd[Minute(1)]);
    }

    /// <summary>
    /// NULL-дельта в окне (свеча до миграции 012) — окно возвращается пустым, тики НЕ
    /// читаются. Одна неизвестная дельта делала бы неверными все суммы после себя, а
    /// тиковый путь запрещён по конструкции: 16.07.2026 три зависших CVD-скана по тикам
    /// остановили слив очереди агрегации до нуля. Потеря временная — переагрегация
    /// минуты заполняет дельту, и фичи пересчитываются уже с CVD.
    /// </summary>
    [Fact]
    public async Task GetCvd_ReturnsEmptyWithoutTouchingTicks_WhenWindowHasUncomputedDelta()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 2.0m, false),
            (2, Minute(0) + 2_000, 101m, 0.5m, true),
            (3, Minute(1) + 1_000, 102m, 0.7m, true));

        await _tradeRepository.AggregateDirtyMinutesAsync(100);

        // Свеча «до миграции»: дельту у неё никто не считал.
        await ExecuteAsync(
            @"UPDATE public.""Ohlcv_1min"" SET ""CvdDelta"" = NULL WHERE ""OpenTime"" = @T;",
            new { T = Minute(0) });

        var cvd = await GetCvdAsync();

        Assert.Empty(cvd);
    }

    // --- вспомогательное ---

    private static long Minute(int offset) => Jan2026Ms + offset * 60_000L;

    private async Task<Dictionary<long, decimal>> GetCvdAsync()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Minute(0)).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(Minute(2)).UtcDateTime;

        return (await _analysisRepository.GetCvdForOhlcvAsync(Symbol, start, end))
            .ToDictionary(c => c.OpenTime, c => c.Cvd);
    }

    private async Task InsertTradesAsync(
        params (long Id, long Time, decimal Price, decimal Qty, bool IsBuyerMaker)[] trades)
    {
        await ExecuteAsync(
            @"SELECT public.sp_bulk_insert_trades(
                  @Ids, @Symbols, @Prices, @Quantities, @QuoteQuantities,
                  @Times, @IsBuyerMakers, @IsBestMatches);",
            new
            {
                Ids = trades.Select(t => t.Id).ToArray(),
                Symbols = trades.Select(_ => Symbol).ToArray(),
                Prices = trades.Select(t => t.Price).ToArray(),
                Quantities = trades.Select(t => t.Qty).ToArray(),
                QuoteQuantities = trades.Select(t => t.Price * t.Qty).ToArray(),
                Times = trades.Select(t => t.Time).ToArray(),
                IsBuyerMakers = trades.Select(t => t.IsBuyerMaker).ToArray(),
                IsBestMatches = trades.Select(_ => true).ToArray()
            });
    }

    private async Task ExecuteAsync(string sql, object? param = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, param);
    }

    private async Task<T> QuerySingleAsync<T>(string sql, object? param = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<T>(sql, param);
    }
}
