using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Проверяет, что проверки качества данных действительно находят порчу.
/// В контейнер с боевым baseline'ом схемы сажаются заведомо битые данные,
/// затем прогоняются проверки и сверяется, что каждая нашла свою поломку.
/// </summary>
public sealed class DataQualityRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private DataQualityRepository _repository = null!;
    private string _connectionString = null!;

    // 2025-01-01T00:00:00Z — попадает в партицию Trades_2025_01.
    private const long Jan2025Ms = 1_735_689_600_000;
    private static readonly DateTime PeriodFrom = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodTo = new(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc);

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

        _repository = new DataQualityRepository(configuration);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task RunTradesChecks_FindsGapsInvalidPricesDuplicatesAndUntrackedSymbols()
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        await db.ExecuteAsync(
            @"INSERT INTO public.""TrackedSymbols"" (""Symbol"", ""IsActive"") VALUES ('BTCUSDT', true);");

        // TradeId 3 пропущен → один разрыв последовательности.
        // TradeId 5 — цена 0 → невалидная цена.
        // TradeId 6 продублирован с другим TradeTime → дубликат (PK это допускает: тройка).
        // GHOSTUSDT нет в TrackedSymbols → неотслеживаемый символ.
        await db.ExecuteAsync(
            @"INSERT INTO public.""Trades""
                (""TradeId"", ""Symbol"", ""Price"", ""Quantity"", ""QuoteQuantity"", ""TradeTime"", ""IsBuyerMaker"", ""IsBestMatch"")
              VALUES
                (1, 'BTCUSDT', 100, 1, 100, @T0, false, true),
                (2, 'BTCUSDT', 101, 1, 101, @T1, false, true),
                (4, 'BTCUSDT', 102, 1, 102, @T2, false, true),
                (5, 'BTCUSDT',   0, 1,   0, @T3, false, true),
                (6, 'BTCUSDT', 103, 1, 103, @T4, false, true),
                (6, 'BTCUSDT', 103, 1, 103, @T5, false, true),
                (7, 'GHOSTUSDT', 50, 1, 50, @T6, false, true);",
            new
            {
                T0 = Ms(0), T1 = Ms(1), T2 = Ms(2), T3 = Ms(3),
                T4 = Ms(4), T5 = Ms(5), T6 = Ms(6)
            });

        var findings = await _repository.RunTradesChecksAsync(null, PeriodFrom, PeriodTo);

        Assert.Equal(1, Count(findings, "trade_id_gaps"));                  // 3 пропущен
        Assert.Equal(1, Count(findings, "invalid_price_or_quantity"));      // цена 0
        Assert.Equal(1, Count(findings, "duplicate_trade_id"));             // TradeId 6 дважды
        Assert.Equal(1, Count(findings, "untracked_symbol"));               // GHOSTUSDT
        Assert.Equal(0, Count(findings, "trade_time_in_future"));

        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "invalid_price_or_quantity"));
        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "duplicate_trade_id"));
        Assert.Equal(DataQualityChecks.SeverityWarning, Severity(findings, "trade_id_gaps"));

        // Детали неотслеживаемого символа должны называть виновника.
        var untracked = findings.Single(f => f.CheckType == "untracked_symbol");
        Assert.Contains("GHOSTUSDT", untracked.Details);
    }

    [Fact]
    public async Task RunOhlcvChecks_FindsBrokenInvariantsMisalignmentAndMissingMinutes()
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        // Свеча 1: корректная.
        // Свеча 2: High < Low.
        // Свеча 3: Close вне [Low, High].
        // Свеча 4: OpenTime не кратен минуте (+30 сек).
        // Между свечами 1 и 2 пропущена одна минута.
        await db.ExecuteAsync(
            @"INSERT INTO public.""Ohlcv_1min""
                (""Symbol"", ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume"")
              VALUES
                ('BTCUSDT', @T0,  100, 105,  99, 103, 10),
                ('BTCUSDT', @T2,  100,  90, 110, 100, 10),
                ('BTCUSDT', @T3,  100, 105,  99, 200, 10),
                ('BTCUSDT', @T4x, 100, 105,  99, 103, 10);",
            new
            {
                T0  = Ms(0),
                T2  = Ms(2),            // минута 1 пропущена
                T3  = Ms(3),
                T4x = Ms(4) + 30_000    // не выровнен по минуте
            });

        var findings = await _repository.RunOhlcvChecksAsync("BTCUSDT", PeriodFrom, PeriodTo);

        Assert.Equal(1, Count(findings, "high_below_low"));
        Assert.Equal(1, Count(findings, "open_close_outside_range"));
        Assert.Equal(1, Count(findings, "opentime_not_minute_aligned"));
        Assert.Equal(0, Count(findings, "negative_volume"));

        // Между T0 и T2 пропущена ровно одна минута.
        Assert.True(Count(findings, "missing_minutes") >= 1);

        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "high_below_low"));
        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "opentime_not_minute_aligned"));
    }

    [Fact]
    public async Task RunFeaturesChecks_FindsRsiOutOfRangeOrphansAndProcessedCandlesWithoutFeatures()
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        // Свеча помечена processed, но индикаторов для неё нет — тихая потеря.
        await db.ExecuteAsync(
            @"INSERT INTO public.""Ohlcv_1min""
                (""Symbol"", ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume"", ""ProcessingStatus"")
              VALUES ('BTCUSDT', @T0, 100, 105, 99, 103, 10, 'processed');",
            new { T0 = Ms(0) });

        // RSI = 150 — вне [0, 100]. И это индикатор без свечи → сирота.
        await db.ExecuteAsync(
            @"INSERT INTO public.""Ohlcv_Features"" (""Symbol"", ""OpenTime"", ""RSI_14"")
              VALUES ('BTCUSDT', @T5, 150);",
            new { T5 = Ms(5) });

        var findings = await _repository.RunFeaturesChecksAsync("BTCUSDT", PeriodFrom, PeriodTo);

        Assert.Equal(1, Count(findings, "rsi_out_of_range"));
        Assert.Equal(1, Count(findings, "orphan_features"));
        Assert.Equal(1, Count(findings, "processed_candle_without_features"));

        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "rsi_out_of_range"));
        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "processed_candle_without_features"));
    }

    [Fact]
    public async Task RunPipelineChecks_FindsWatermarkAheadOfDataAndExhaustedAuditRetries()
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        await db.ExecuteAsync(
            @"INSERT INTO public.""Trades""
                (""TradeId"", ""Symbol"", ""Price"", ""Quantity"", ""QuoteQuantity"", ""TradeTime"", ""IsBuyerMaker"", ""IsBestMatch"")
              VALUES (1, 'BTCUSDT', 100, 1, 100, @T0, false, true);",
            new { T0 = Ms(0) });

        // Watermark обогнал данные на сутки — всё, что позади, выпадает из обработки молча.
        await db.ExecuteAsync(
            @"INSERT INTO public.""Processing_Watermarks""
                (""ProcessName"", ""LastProcessedTimestamp"", ""Status"", ""LastUpdate_UTC"")
              VALUES ('OhlcvAggregator', @Ahead, 'Pending', NOW());",
            new { Ahead = Ms(0) + 86_400_000 });

        // FeatureCalculator вообще отсутствует → watermark_missing.

        // Символ исчерпал попытки аудита: по текущему запросу выборки он больше
        // никогда не попадёт в аудит и зависнет непроверенным.
        await db.ExecuteAsync(
            @"INSERT INTO public.""HistoricalAudit_Watermarks""
                (""Symbol"", ""LastChecked_TradeId"", ""LastChecked_Timestamp"", ""Status"", ""RetryCount"")
              VALUES ('DEADUSDT', 0, 0, 'Failed', 9);");

        var findings = await _repository.RunPipelineChecksAsync();

        Assert.Equal(1, Count(findings, "watermark_ahead_of_data"));
        Assert.Equal(1, Count(findings, "watermark_missing"));       // FeatureCalculator
        Assert.Equal(1, Count(findings, "audit_retries_exhausted")); // DEADUSDT

        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "watermark_ahead_of_data"));
        Assert.Equal(DataQualityChecks.SeverityError, Severity(findings, "audit_retries_exhausted"));

        var exhausted = findings.Single(f => f.CheckType == "audit_retries_exhausted");
        Assert.Contains("DEADUSDT", exhausted.Details);
    }

    [Fact]
    public async Task RunChecks_CleanData_ReportsNoProblems()
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        await db.ExecuteAsync(
            @"INSERT INTO public.""TrackedSymbols"" (""Symbol"", ""IsActive"") VALUES ('BTCUSDT', true);");

        await db.ExecuteAsync(
            @"INSERT INTO public.""Trades""
                (""TradeId"", ""Symbol"", ""Price"", ""Quantity"", ""QuoteQuantity"", ""TradeTime"", ""IsBuyerMaker"", ""IsBestMatch"")
              VALUES
                (1, 'BTCUSDT', 100, 1, 100, @T0, false, true),
                (2, 'BTCUSDT', 101, 1, 101, @T1, false, true),
                (3, 'BTCUSDT', 102, 1, 102, @T2, false, true);",
            new { T0 = Ms(0), T1 = Ms(1), T2 = Ms(2) });

        var findings = await _repository.RunTradesChecksAsync("BTCUSDT", PeriodFrom, PeriodTo);

        Assert.DoesNotContain(findings, f => f.Severity != DataQualityChecks.SeverityOk);
    }

    [Fact]
    public async Task RunChecks_RangeLongerThanMonth_IsRejected()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(45);   // больше 31 дня

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.RunTradesChecksAsync("BTCUSDT", from, to));

        Assert.Contains("31", ex.Message);
    }

    [Fact]
    public async Task SaveAndGetFindings_RoundTrips()
    {
        var findings = new[]
        {
            new DataQualityFinding
            {
                CheckGroup = DataQualityChecks.GroupTrades,
                CheckType = "trade_id_gaps",
                Symbol = "BTCUSDT",
                PeriodFrom = PeriodFrom,
                PeriodTo = PeriodTo,
                Severity = DataQualityChecks.SeverityWarning,
                Count = 42,
                Details = "{\"sample\": [1, 2, 3]}",
                CheckedAt = DateTime.UtcNow
            }
        };

        await _repository.SaveFindingsAsync(findings);

        var stored = (await _repository.GetFindingsAsync()).ToList();

        var single = Assert.Single(stored);
        Assert.Equal("trade_id_gaps", single.CheckType);
        Assert.Equal("BTCUSDT", single.Symbol);
        Assert.Equal(42, single.Count);
        Assert.Equal(DataQualityChecks.SeverityWarning, single.Severity);
        Assert.Contains("sample", single.Details);

        // Фильтр по статусу отсекает несовпадающее.
        var errorsOnly = await _repository.GetFindingsAsync(severity: DataQualityChecks.SeverityError);
        Assert.Empty(errorsOnly);
    }

    private static long Ms(int minuteOffset) => Jan2025Ms + minuteOffset * 60_000L;

    private static long Count(IReadOnlyList<DataQualityFinding> findings, string checkType) =>
        findings.Single(f => f.CheckType == checkType).Count;

    private static string Severity(IReadOnlyList<DataQualityFinding> findings, string checkType) =>
        findings.Single(f => f.CheckType == checkType).Severity;
}
