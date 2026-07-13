using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Свечи старших таймфреймов собираются из минутных на лету. Проверяем, что
/// сетка баров и агрегаты OHLCV считаются правильно, включая недельную сетку:
/// Unix-эпоха начинается в четверг, а биржевая неделя — в понедельник.
/// </summary>
public sealed class ChartRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private ChartRepository _repository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";

    // 2026-01-05T00:00:00Z — понедельник. Удобная точка отсчёта для недельной сетки.
    private const long Monday5Jan2026Ms = 1_767_571_200_000;

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

        _repository = new ChartRepository(configuration);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task GetCandles_Aggregates15Minutes_FromMinuteCandles()
    {
        // 15 минутных свечей: цена растёт 100 → 114, объём по 1.
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 15, startPrice: 100m);

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.M15, limit: 10);

        var bar = Assert.Single(candles);
        Assert.Equal(Monday5Jan2026Ms, bar.OpenTime);
        Assert.Equal(100m, bar.OpenPrice);   // первая минута
        Assert.Equal(114m, bar.ClosePrice);  // последняя минута
        Assert.Equal(115m, bar.HighPrice);   // High = цена + 1 (см. SeedMinutesAsync)
        Assert.Equal(99m, bar.LowPrice);     // Low  = первая цена - 1
        Assert.Equal(15m, bar.Volume);       // сумма объёмов
    }

    [Fact]
    public async Task GetCandles_SplitsIntoSeparateBars_OnBucketBoundary()
    {
        // 30 минут → ровно два 15-минутных бара.
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 30, startPrice: 100m);

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.M15, limit: 10);

        Assert.Equal(2, candles.Count);
        Assert.Equal(Monday5Jan2026Ms, candles[0].OpenTime);
        Assert.Equal(Monday5Jan2026Ms + 900_000, candles[1].OpenTime);

        Assert.Equal(100m, candles[0].OpenPrice);
        Assert.Equal(114m, candles[0].ClosePrice);
        Assert.Equal(115m, candles[1].OpenPrice);
        Assert.Equal(129m, candles[1].ClosePrice);
    }

    [Fact]
    public async Task GetCandles_HourlyBars_AlignToTheHour()
    {
        // Начинаем с 00:30 — свеча должна попасть в час 00:00, а не открыть новый бар.
        await SeedMinutesAsync(Monday5Jan2026Ms + 30 * 60_000L, count: 30, startPrice: 100m);

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.H1, limit: 10);

        var bar = Assert.Single(candles);
        Assert.Equal(Monday5Jan2026Ms, bar.OpenTime); // 00:00, не 00:30
    }

    [Fact]
    public async Task GetCandles_WeeklyBars_OpenOnMonday_NotOnEpochThursday()
    {
        // Сутки минуток начиная с понедельника.
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 60, startPrice: 100m);
        // И ещё сутки со среды той же недели — должны попасть в ТОТ ЖЕ недельный бар.
        await SeedMinutesAsync(Monday5Jan2026Ms + 2 * 86_400_000L, count: 60, startPrice: 200m, tradeIdOffset: 10_000);

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.W1, limit: 10);

        var bar = Assert.Single(candles);

        // Бар обязан открываться в понедельник 00:00 UTC. Если бы сетка была выровнена
        // по эпохе, неделя открывалась бы в четверг и данные разъехались бы на два бара.
        var openTime = DateTimeOffset.FromUnixTimeMilliseconds(bar.OpenTime).UtcDateTime;
        Assert.Equal(DayOfWeek.Monday, openTime.DayOfWeek);
        Assert.Equal(Monday5Jan2026Ms, bar.OpenTime);

        Assert.Equal(100m, bar.OpenPrice);   // первая минута понедельника
        Assert.Equal(259m, bar.ClosePrice);  // последняя минута среды
        Assert.Equal(120m, bar.Volume);      // 60 + 60 минут
    }

    [Fact]
    public async Task GetCandlesSince_ReturnsOnlyBarsFromGivenTime()
    {
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 180, startPrice: 100m); // 3 часа

        var all = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.H1, limit: 10);
        Assert.Equal(3, all.Count);

        var since = await _repository.GetCandlesSinceAsync(Symbol, ChartTimeframes.H1, all[1].OpenTime);

        Assert.Equal(2, since.Count);                        // второй и третий бар
        Assert.Equal(all[1].OpenTime, since[0].OpenTime);    // включая сам бар since
        Assert.Equal(all[2].OpenTime, since[1].OpenTime);
    }

    [Fact]
    public async Task GetCandles_ReturnsMostRecentBars_WhenLimitIsSmaller()
    {
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 300, startPrice: 100m); // 5 часов

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.H1, limit: 2);

        Assert.Equal(2, candles.Count);
        // Отдаём последние бары, но в хронологическом порядке.
        Assert.True(candles[0].OpenTime < candles[1].OpenTime);
        Assert.Equal(Monday5Jan2026Ms + 4 * 3_600_000L, candles[1].OpenTime);
    }

    [Fact]
    public async Task GetCvd_TakesLastValueInBar_NotSum()
    {
        // CVD кумулятивен: за час значение на конец бара — последнее минутное, а не сумма.
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        for (var i = 0; i < 60; i++)
        {
            await db.ExecuteAsync(
                @"INSERT INTO public.""Ohlcv_Features"" (""Symbol"", ""OpenTime"", ""CVD"") VALUES (@S, @T, @C);",
                new { S = Symbol, T = Monday5Jan2026Ms + i * 60_000L, C = 10m + i });
        }

        var cvd = await _repository.GetCvdAsync(
            Symbol, ChartTimeframes.H1, Monday5Jan2026Ms, Monday5Jan2026Ms + 3_600_000L);

        var point = Assert.Single(cvd);
        Assert.Equal(Monday5Jan2026Ms, point.OpenTime);
        Assert.Equal(69m, point.Value); // 10 + 59 — последняя минута, не сумма
    }

    [Fact]
    public async Task Indicators_AreComputedOnSelectedTimeframe_NotOnMinutes()
    {
        // 100 часов минуток → 100 часовых баров.
        await SeedMinutesAsync(Monday5Jan2026Ms, count: 100 * 60, startPrice: 100m);

        var candles = await _repository.GetCandlesAsync(Symbol, ChartTimeframes.H1, limit: 200);
        Assert.Equal(100, candles.Count);

        var service = new ChartIndicatorService();
        var indicators = service.Calculate(candles, new IndicatorSettings
        {
            RsiPeriod = 14,
            MaFastPeriod = 10,
            MaSlowPeriod = 50
        });

        // RSI появляется после периода прогрева и лежит в [0, 100].
        Assert.NotEmpty(indicators.Rsi);
        Assert.All(indicators.Rsi, p => Assert.InRange(p.Value!.Value, 0m, 100m));

        // SMA(10) на 100 барах даёт 91 значение (первые 9 — прогрев).
        Assert.Equal(91, indicators.MaFast.Count);
        Assert.Equal(51, indicators.MaSlow.Count);

        // Точки индикаторов привязаны к барам таймфрейма, а не к минутам.
        var barTimes = candles.Select(c => c.OpenTime).ToHashSet();
        Assert.All(indicators.MaFast, p => Assert.Contains(p.OpenTime, barTimes));

        Assert.NotEmpty(indicators.MacdLine);
        Assert.NotEmpty(indicators.MacdHistogram);
    }

    [Fact]
    public async Task GetCandles_UnknownTimeframe_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _repository.GetCandlesAsync(Symbol, "3m", limit: 10));
    }

    /// <summary>
    /// Минутные свечи: цена растёт на 1 за минуту, High = цена + 1, Low = первая цена - 1,
    /// объём = 1. Такая форма делает ожидаемые агрегаты очевидными.
    /// </summary>
    private async Task SeedMinutesAsync(long startMs, int count, decimal startPrice, int tradeIdOffset = 0)
    {
        await using var db = new NpgsqlConnection(_connectionString);
        await db.OpenAsync();

        var rows = Enumerable.Range(0, count).Select(i => new
        {
            Symbol,
            OpenTime = startMs + i * 60_000L,
            OpenPrice = startPrice + i,
            HighPrice = startPrice + i + 1,
            LowPrice = startPrice - 1,
            ClosePrice = startPrice + i,
            Volume = 1m
        });

        await db.ExecuteAsync(
            @"INSERT INTO public.""Ohlcv_1min""
                (""Symbol"", ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume"")
              VALUES (@Symbol, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume)
              ON CONFLICT (""Symbol"", ""OpenTime"") DO NOTHING;",
            rows);
    }
}
