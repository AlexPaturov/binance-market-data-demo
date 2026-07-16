using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Захват свечей расчётом фич и возврат из 'processing' обратно в работу (миграция 011).
///
/// Раньше 'processing' был билетом в один конец: свечи воркера, убитого посреди пачки,
/// и свечи упавших символов оставались в нём навсегда и без фич. Теперь захват пишет
/// `ClaimedAt`, и протухший захват снова становится кандидатом.
/// </summary>
public sealed class OhlcvClaimReclaimTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private OhlcvRepository _repository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;   // 2026-01-01T00:00:00Z

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

        _repository = new OhlcvRepository(configuration);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Claim_TakesNewKline_AndStampsClaimedAt()
    {
        await InsertCandleAsync(Minute(0), "new");

        var claimed = (await _repository.ClaimNewKlinesForProcessingAsync(10)).ToList();

        Assert.Single(claimed);

        var (status, claimedAt) = await StatusAsync(Minute(0));
        Assert.Equal("processing", status);
        Assert.NotNull(claimedAt);
    }

    /// <summary>Свежий захват — чужая работа: второй заход её не трогает.</summary>
    [Fact]
    public async Task Claim_DoesNotTakeFreshlyClaimedKline()
    {
        await InsertCandleAsync(Minute(0), "new");
        await _repository.ClaimNewKlinesForProcessingAsync(10);

        var second = await _repository.ClaimNewKlinesForProcessingAsync(10);

        Assert.Empty(second);
    }

    /// <summary>
    /// Протухший захват возвращается в работу: так достаются свечи воркера, убитого
    /// посреди пачки, и свечи символов, на которых расчёт упал.
    /// </summary>
    [Fact]
    public async Task Claim_ReclaimsStaleProcessingKline()
    {
        await InsertCandleAsync(Minute(0), "new");
        await _repository.ClaimNewKlinesForProcessingAsync(10);

        // Захват старше TTL (15 минут) — воркер, который его держал, мёртв.
        await ExecuteAsync(
            @"UPDATE public.""Ohlcv_1min""
              SET ""ClaimedAt"" = now() - interval '20 minutes';");

        var reclaimed = (await _repository.ClaimNewKlinesForProcessingAsync(10)).ToList();

        Assert.Single(reclaimed);
        Assert.Equal(Minute(0), reclaimed[0].OpenTime);
    }

    /// <summary>
    /// 'processing' без ClaimedAt — захват кода до миграции 011: возраст неизвестен,
    /// считается протухшим. Иначе такие свечи не вернулись бы в работу никогда.
    /// </summary>
    [Fact]
    public async Task Claim_ReclaimsProcessingKline_WithNullClaimedAt()
    {
        await InsertCandleAsync(Minute(0), "processing");

        var reclaimed = (await _repository.ClaimNewKlinesForProcessingAsync(10)).ToList();

        Assert.Single(reclaimed);

        var (_, claimedAt) = await StatusAsync(Minute(0));
        Assert.NotNull(claimedAt);
    }

    [Fact]
    public async Task Claim_DoesNotTouchProcessedKlines()
    {
        await InsertCandleAsync(Minute(0), "processed");

        var claimed = await _repository.ClaimNewKlinesForProcessingAsync(10);

        Assert.Empty(claimed);
    }

    // --- вспомогательное ---

    private static long Minute(int offset) => Jan2026Ms + offset * 60_000L;

    private async Task InsertCandleAsync(long openTime, string status)
    {
        await ExecuteAsync($@"
            SELECT public.sp_ensure_month_partitions({Jan2026Ms});
            INSERT INTO public.""Ohlcv_1min""
                (""Symbol"", ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume"", ""ProcessingStatus"")
            VALUES ('{Symbol}', {openTime}, 100, 100, 100, 100, 1, '{status}');");
    }

    private async Task<(string Status, DateTime? ClaimedAt)> StatusAsync(long openTime)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<(string, DateTime?)>(
            @"SELECT ""ProcessingStatus"", ""ClaimedAt"" FROM public.""Ohlcv_1min"" WHERE ""OpenTime"" = @T;",
            new { T = openTime });
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }
}
