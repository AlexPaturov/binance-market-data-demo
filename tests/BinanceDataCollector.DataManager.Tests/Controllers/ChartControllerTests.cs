using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BinanceDataCollector.DataManager.Tests.Controllers;

/// <summary>
/// График опрашивает сервер раз в 3 секунды и просит только бары новее последнего
/// известного (`sinceMs`). Считать индикаторы по одним лишь новым барам нельзя:
/// RSI(14), MACD(26) и MA(200) требуют прогрева и по двум барам вернут пустоту —
/// линии индикаторов замерли бы после первой загрузки, а свечи продолжали бы двигаться.
/// </summary>
public class ChartControllerTests
{
    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;
    private const long HourMs = 3_600_000;

    private readonly Mock<IChartRepository> _chartRepo = new();
    private readonly Mock<ITrackedSymbolRepository> _symbolRepo = new();

    [Fact]
    public async Task Data_WhenPolling_RequestsHistoryBeforeSince_ForIndicatorWarmup()
    {
        var since = Jan2026Ms + 500 * HourMs;
        long requestedFrom = 0;

        _chartRepo
            .Setup(r => r.GetCandlesSinceAsync(Symbol, ChartTimeframes.H1, It.IsAny<long>()))
            .Callback<string, string, long>((_, _, from) => requestedFrom = from)
            .ReturnsAsync(new List<ChartCandle>());

        await CreateController().Data(Symbol, ChartTimeframes.H1, sinceMs: since, maSlowPeriod: 200);

        // Просим историю, а не только новые бары: MA(200) без 200 предыдущих баров пуста.
        Assert.True(requestedFrom < since - 200 * HourMs,
            $"История взята с {requestedFrom}, что недостаточно для прогрева MA(200) от {since}.");
    }

    [Fact]
    public async Task Data_WhenPolling_ReturnsIndicatorsForNewBars_NotEmpty()
    {
        // 300 часовых баров: 299 истории + 1 «новый».
        var candles = Enumerable.Range(0, 300)
            .Select(i => Candle(Jan2026Ms + i * HourMs, 100m + i % 7))
            .ToList();

        var since = candles[^1].OpenTime;

        _chartRepo
            .Setup(r => r.GetCandlesSinceAsync(Symbol, ChartTimeframes.H1, It.IsAny<long>()))
            .ReturnsAsync(candles);

        var result = await CreateController().Data(
            Symbol, ChartTimeframes.H1, sinceMs: since, rsiPeriod: 14, maFastPeriod: 50, maSlowPeriod: 200);

        var data = Assert.IsType<ChartData>(Assert.IsType<JsonResult>(result).Value);

        // Отдаём только свежий бар, а не всю подтянутую историю.
        var candle = Assert.Single(data.Candles);
        Assert.Equal(since, candle.OpenTime);

        // И индикаторы по нему — посчитанные на полной истории, а не пустые.
        var rsi = Assert.Single(data.Indicators.Rsi);
        Assert.Equal(since, rsi.OpenTime);
        Assert.InRange(rsi.Value!.Value, 0m, 100m);

        Assert.Equal(since, Assert.Single(data.Indicators.MaSlow).OpenTime);
        Assert.Equal(since, Assert.Single(data.Indicators.MaFast).OpenTime);
        Assert.NotEmpty(data.Indicators.MacdLine);
    }

    [Fact]
    public async Task Data_FullLoad_ReturnsWholeWindow()
    {
        var candles = Enumerable.Range(0, 100)
            .Select(i => Candle(Jan2026Ms + i * HourMs, 100m + i % 5))
            .ToList();

        _chartRepo
            .Setup(r => r.GetCandlesAsync(Symbol, ChartTimeframes.H1, It.IsAny<int>()))
            .ReturnsAsync(candles);

        var result = await CreateController().Data(Symbol, ChartTimeframes.H1);

        var data = Assert.IsType<ChartData>(Assert.IsType<JsonResult>(result).Value);
        Assert.Equal(100, data.Candles.Count);
        Assert.NotEmpty(data.Indicators.Rsi);
    }

    [Fact]
    public async Task Data_UnknownTimeframe_IsRejected()
    {
        var result = await CreateController().Data(Symbol, "3m");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Data_WithoutSymbol_IsRejected()
    {
        var result = await CreateController().Data("", ChartTimeframes.H1);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    private ChartController CreateController() => new(
        _chartRepo.Object,
        new ChartIndicatorService(),   // настоящий расчёт: проверяем именно прогрев
        _symbolRepo.Object);

    private static ChartCandle Candle(long openTime, decimal price) => new()
    {
        OpenTime = openTime,
        OpenPrice = price,
        HighPrice = price + 1m,
        LowPrice = price - 1m,
        ClosePrice = price,
        Volume = 1m
    };
}
