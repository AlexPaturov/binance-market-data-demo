using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common.Auth;
using BinanceDataCollector.DataManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

[Authorize(Policy = DataManagerAuthorizationPolicies.Viewer)]
public class ChartController : Controller
{
    private readonly IChartRepository _chartRepo;
    private readonly IChartIndicatorService _indicators;
    private readonly ITrackedSymbolRepository _symbolRepo;

    public ChartController(
        IChartRepository chartRepo,
        IChartIndicatorService indicators,
        ITrackedSymbolRepository symbolRepo)
    {
        _chartRepo = chartRepo;
        _indicators = indicators;
        _symbolRepo = symbolRepo;
    }

    public async Task<IActionResult> Index()
    {
        var model = new ChartViewModel
        {
            ActiveSymbols = (await _symbolRepo.GetActiveSymbolsAsync()).ToList(),
            Timeframes = ChartTimeframes.All.ToList(),
            DefaultLimit = ChartTimeframes.DefaultLimit
        };

        return View(model);
    }

    /// <summary>
    /// Свечи + индикаторы. Страница дёргает этот метод при смене символа/таймфрейма
    /// и раз в 3 секунды для свежих баров (в последнем случае — с параметром sinceMs,
    /// чтобы не перекачивать весь график).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Data(
        string symbol,
        string timeframe = ChartTimeframes.H1,
        int limit = ChartTimeframes.DefaultLimit,
        long? sinceMs = null,
        int rsiPeriod = 14,
        int macdFast = 12,
        int macdSlow = 26,
        int macdSignal = 9,
        int maFastPeriod = 50,
        int maSlowPeriod = 200,
        bool useEma = false,
        bool withCvd = false)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest("Не указан символ.");

        if (!ChartTimeframes.IsKnown(timeframe))
            return BadRequest($"Неизвестный таймфрейм: {timeframe}. Допустимые: {string.Join(", ", ChartTimeframes.All)}.");

        var candles = sinceMs.HasValue
            ? await _chartRepo.GetCandlesSinceAsync(symbol, timeframe, sinceMs.Value)
            : await _chartRepo.GetCandlesAsync(symbol, timeframe, limit);

        var data = new ChartData { Symbol = symbol, Timeframe = timeframe, Candles = candles };

        if (candles.Count > 0)
        {
            var settings = new IndicatorSettings
            {
                RsiPeriod    = Math.Clamp(rsiPeriod, 2, 200),
                MacdFast     = Math.Clamp(macdFast, 2, 200),
                MacdSlow     = Math.Clamp(macdSlow, 3, 400),
                MacdSignal   = Math.Clamp(macdSignal, 2, 200),
                MaFastPeriod = Math.Clamp(maFastPeriod, 2, 1000),
                MaSlowPeriod = Math.Clamp(maSlowPeriod, 2, 1000),
                UseEma       = useEma
            };

            data.Indicators = _indicators.Calculate(candles, settings);

            if (withCvd)
            {
                data.Indicators.Cvd = await _chartRepo.GetCvdAsync(
                    symbol, timeframe, candles[0].OpenTime, candles[^1].OpenTime + ChartTimeframes.BucketMs(timeframe));
            }
        }

        return Json(data);
    }
}
