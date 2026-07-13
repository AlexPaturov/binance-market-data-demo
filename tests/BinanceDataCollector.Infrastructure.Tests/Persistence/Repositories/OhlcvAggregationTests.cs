using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Агрегация тиков в свечи идёт от СТАТУСА тиков, а не от watermark'а по времени.
///
/// Раньше агрегатор шёл вперёд окнами от watermark'а, и всё, что вставлялось «позади»
/// уже пройденной отметки, не агрегировалось никогда. А старые тики вставляют штатные
/// пути: закрытие дыр (FillGapWorker), импорт архивов вразнобой (CsvImportWorker),
/// историческая дозагрузка. То есть закрытая дыра не попадала в свечи.
///
/// Эти тесты фиксируют, что порядок прихода данных больше не имеет значения.
/// </summary>
public sealed class OhlcvAggregationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _repository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;   // 2026-01-01T00:00:00Z
    private const long SixHoursMs = 21_600_000;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _connectionString = _db.GetConnectionString();

        var schemaSql = await File.ReadAllTextAsync("02_schema.sql");
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

        _repository = new TradeRepository(configuration, NullLogger<TradeRepository>.Instance);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Aggregation_BuildsCandle_FromTicksOfTheMinute()
    {
        // Одна минута, четыре сделки. Open — цена первой, Close — последней.
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 1m),
            (2, Minute(0) + 2_000, 105m, 2m),
            (3, Minute(0) + 3_000,  95m, 3m),
            (4, Minute(0) + 4_000, 102m, 4m));

        var candles = await _repository.AggregateNewTradesAsync(SixHoursMs);
        Assert.Equal(1, candles);

        var candle = await SingleCandleAsync(Minute(0));

        Assert.Equal(100m, candle.OpenPrice);   // первая сделка
        Assert.Equal(105m, candle.HighPrice);
        Assert.Equal(95m,  candle.LowPrice);
        Assert.Equal(102m, candle.ClosePrice);  // последняя сделка
        Assert.Equal(10m,  candle.Volume);      // 1+2+3+4
    }

    /// <summary>
    /// Главный сценарий, ради которого всё переписывалось: тик, приехавший ПОСЛЕ того,
    /// как минута уже была агрегирована (закрытие дыры), должен попасть в свечу.
    /// В старой схеме он оставался бы 'new' навсегда, а свеча — неполной.
    /// </summary>
    [Fact]
    public async Task Aggregation_PicksUpBackfilledTicks_AfterMinuteWasAlreadyAggregated()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 1m),
            (3, Minute(0) + 3_000, 102m, 1m));

        await _repository.AggregateNewTradesAsync(SixHoursMs);

        var before = await SingleCandleAsync(Minute(0));
        Assert.Equal(102m, before.ClosePrice);
        Assert.Equal(2m,   before.Volume);

        // Позже приезжает пропущенная сделка №2 — она между уже обработанными,
        // и её цена выше всех: свеча обязана пересчитаться.
        await InsertTradesAsync((2, Minute(0) + 2_000, 150m, 5m));

        var candles = await _repository.AggregateNewTradesAsync(SixHoursMs);
        Assert.Equal(1, candles);

        var after = await SingleCandleAsync(Minute(0));

        Assert.Equal(100m, after.OpenPrice);   // всё ещё первая сделка
        Assert.Equal(150m, after.HighPrice);   // максимум подтянулся
        Assert.Equal(102m, after.ClosePrice);  // последняя по времени — по-прежнему №3
        Assert.Equal(7m,   after.Volume);      // 1+5+1, пересчитано целиком, а не досуммировано
    }

    /// <summary>
    /// Тики, приехавшие «позади» уже обработанного участка (архивы импортируются
    /// джобами вразнобой), не должны потеряться: окно всегда начинается с самого
    /// старого необработанного тика, а не от watermark'а.
    /// </summary>
    [Fact]
    public async Task Aggregation_ProcessesOlderTicks_ArrivingAfterNewerOnesWereDone()
    {
        // Сначала приезжает поздний час.
        await InsertTradesAsync((100, Minute(300) + 1_000, 200m, 1m));
        await _repository.AggregateNewTradesAsync(SixHoursMs);

        Assert.Equal(1, await CandleCountAsync());

        // Потом — ранний. Watermark уже «прошёл» это время.
        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));

        var candles = await _repository.AggregateNewTradesAsync(SixHoursMs);

        Assert.Equal(1, candles);
        Assert.Equal(2, await CandleCountAsync());

        var early = await SingleCandleAsync(Minute(0));
        Assert.Equal(100m, early.ClosePrice);
    }

    [Fact]
    public async Task Aggregation_MarksRecomputedCandleAsNew_SoIndicatorsAreRecalculated()
    {
        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));
        await _repository.AggregateNewTradesAsync(SixHoursMs);

        // Индикаторы посчитаны — свеча обработана.
        await ExecuteAsync(
            @"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");

        // Приезжает пропущенный тик → свеча пересчитывается → индикаторы устарели.
        await InsertTradesAsync((2, Minute(0) + 2_000, 150m, 1m));
        await _repository.AggregateNewTradesAsync(SixHoursMs);

        var status = await QuerySingleAsync<string>(
            @"SELECT ""ProcessingStatus"" FROM public.""Ohlcv_1min"" WHERE ""OpenTime"" = @T;",
            new { T = Minute(0) });

        Assert.Equal("new", status);
    }

    [Fact]
    public async Task Aggregation_IsIdempotent_SecondRunChangesNothing()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 1m),
            (2, Minute(0) + 2_000, 110m, 2m));

        Assert.Equal(1, await _repository.AggregateNewTradesAsync(SixHoursMs));

        // Необработанных тиков не осталось — второй прогон не делает ничего.
        Assert.Equal(0, await _repository.AggregateNewTradesAsync(SixHoursMs));

        var candle = await SingleCandleAsync(Minute(0));
        Assert.Equal(3m, candle.Volume);   // объём не удвоился
        Assert.Equal(1, await CandleCountAsync());
    }

    [Fact]
    public async Task Aggregation_MarksProcessedTicks_LeavingNothingBehind()
    {
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 1m),
            (2, Minute(5) + 1_000, 110m, 1m));

        await _repository.AggregateNewTradesAsync(SixHoursMs);

        var unprocessed = await QuerySingleAsync<long>(
            @"SELECT count(*) FROM public.""Trades"" WHERE ""ProcessingStatus"" = 'new';");

        Assert.Equal(0, unprocessed);
    }

    [Fact]
    public async Task Aggregation_WhenNothingUnprocessed_ReturnsZero()
    {
        Assert.Equal(0, await _repository.AggregateNewTradesAsync(SixHoursMs));
    }

    // --- вспомогательное ---

    private static long Minute(int offset) => Jan2026Ms + offset * 60_000L;

    private async Task InsertTradesAsync(params (long Id, long Time, decimal Price, decimal Qty)[] trades)
    {
        await _repository.BulkInsertAsync(trades.Select(t => new Trade
        {
            TradeId = t.Id,
            Symbol = Symbol,
            Price = t.Price,
            Quantity = t.Qty,
            QuoteQuantity = t.Price * t.Qty,
            TradeTime = t.Time,
            IsBuyerMaker = false,
            IsBestMatch = true
        }));
    }

    private async Task<Ohlcv> SingleCandleAsync(long openTime)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        return await db.QuerySingleAsync<Ohlcv>(
            @"SELECT ""Symbol"", ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume""
              FROM public.""Ohlcv_1min"" WHERE ""OpenTime"" = @T;",
            new { T = openTime });
    }

    private async Task<long> CandleCountAsync() =>
        await QuerySingleAsync<long>(@"SELECT count(*) FROM public.""Ohlcv_1min"";");

    private async Task<T> QuerySingleAsync<T>(string sql, object? param = null)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        return await db.QuerySingleAsync<T>(sql, param);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.ExecuteAsync(sql);
    }
}
