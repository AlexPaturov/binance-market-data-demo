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

        List<ChartCandle> candles;

        if (sinceMs.HasValue)
        {
            // Дозагрузка свежих баров. Индикаторы нельзя считать по одним только новым
            // барам: RSI(14), MACD(26) и MA(200) требуют прогрева и по двум барам вернут
            // пустоту — линии на графике замерли бы после первой загрузки.
            // Поэтому берём историю с запасом, считаем по ней, а отдаём только свежее.
            var warmupBars = WarmupBars(settings);
            var warmupFrom = sinceMs.Value - warmupBars * ChartTimeframes.BucketMs(timeframe);

            var withHistory = await _chartRepo.GetCandlesSinceAsync(symbol, timeframe, warmupFrom);

            var data = new ChartData { Symbol = symbol, Timeframe = timeframe };

            if (withHistory.Count > 0)
            {
                var indicators = _indicators.Calculate(withHistory, settings);

                data.Candles = withHistory.Where(c => c.OpenTime >= sinceMs.Value).ToList();
                data.Indicators = TrimTo(indicators, sinceMs.Value);

                if (withCvd && data.Candles.Count > 0)
                {
                    data.Indicators.Cvd = await _chartRepo.GetCvdAsync(
                        symbol, timeframe, sinceMs.Value,
                        data.Candles[^1].OpenTime + ChartTimeframes.BucketMs(timeframe));
                }
            }

            return Json(data);
        }

        candles = await _chartRepo.GetCandlesAsync(symbol, timeframe, limit);

        var full = new ChartData { Symbol = symbol, Timeframe = timeframe, Candles = candles };

        if (candles.Count > 0)
        {
            full.Indicators = _indicators.Calculate(candles, settings);

            if (withCvd)
            {
                full.Indicators.Cvd = await _chartRepo.GetCvdAsync(
                    symbol, timeframe, candles[0].OpenTime, candles[^1].OpenTime + ChartTimeframes.BucketMs(timeframe));
            }
        }

        return Json(full);
    }

    /// <summary>
    /// Сколько баров истории нужно, чтобы индикаторы вышли из прогрева.
    /// Берём самый требовательный из включённых и добавляем запас.
    /// </summary>
    private static int WarmupBars(IndicatorSettings s) =>
        Math.Max(
            Math.Max(s.RsiPeriod, s.MacdSlow + s.MacdSignal),
            Math.Max(s.MaFastPeriod, s.MaSlowPeriod)) + 10;

    /// <summary>Оставляет только точки, начиная с указанного бара: история нужна была лишь для прогрева.</summary>
    private static ChartIndicators TrimTo(ChartIndicators source, long sinceMs) => new()
    {
        Rsi           = source.Rsi.Where(p => p.OpenTime >= sinceMs).ToList(),
        MacdLine      = source.MacdLine.Where(p => p.OpenTime >= sinceMs).ToList(),
        MacdSignal    = source.MacdSignal.Where(p => p.OpenTime >= sinceMs).ToList(),
        MacdHistogram = source.MacdHistogram.Where(p => p.OpenTime >= sinceMs).ToList(),
        MaFast        = source.MaFast.Where(p => p.OpenTime >= sinceMs).ToList(),
        MaSlow        = source.MaSlow.Where(p => p.OpenTime >= sinceMs).ToList(),
        Cvd           = source.Cvd.Where(p => p.OpenTime >= sinceMs).ToList()
    };
}
