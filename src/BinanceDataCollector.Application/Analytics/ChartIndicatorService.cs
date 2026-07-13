using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Interfaces;
using Skender.Stock.Indicators;

namespace BinanceDataCollector.Application.Analytics;

/// <inheritdoc />
public class ChartIndicatorService : IChartIndicatorService
{
    public ChartIndicators Calculate(IReadOnlyList<ChartCandle> candles, IndicatorSettings settings)
    {
        var result = new ChartIndicators();
        if (candles.Count == 0) return result;

        var quotes = candles
            .Select(c => new Quote
            {
                Date   = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime).UtcDateTime,
                Open   = c.OpenPrice,
                High   = c.HighPrice,
                Low    = c.LowPrice,
                Close  = c.ClosePrice,
                Volume = c.Volume
            })
            .OrderBy(q => q.Date)
            .ToList();

        var rsi = quotes.GetRsi(settings.RsiPeriod).ToList();
        var macd = quotes.GetMacd(settings.MacdFast, settings.MacdSlow, settings.MacdSignal).ToList();

        // Периоды MA заданы в барах текущего таймфрейма: MA(200) на дневных — это 200 дней.
        var maFast = settings.UseEma
            ? quotes.GetEma(settings.MaFastPeriod).Select(e => (e.Date, Value: e.Ema)).ToList()
            : quotes.GetSma(settings.MaFastPeriod).Select(s => (s.Date, Value: s.Sma)).ToList();

        var maSlow = settings.UseEma
            ? quotes.GetEma(settings.MaSlowPeriod).Select(e => (e.Date, Value: e.Ema)).ToList()
            : quotes.GetSma(settings.MaSlowPeriod).Select(s => (s.Date, Value: s.Sma)).ToList();

        result.Rsi           = Points(rsi.Select(r => (r.Date, r.Rsi)));
        result.MacdLine      = Points(macd.Select(m => (m.Date, m.Macd)));
        result.MacdSignal    = Points(macd.Select(m => (m.Date, m.Signal)));
        result.MacdHistogram = Points(macd.Select(m => (m.Date, m.Histogram)));
        result.MaFast        = Points(maFast);
        result.MaSlow        = Points(maSlow);

        return result;
    }

    private static List<IndicatorPoint> Points(IEnumerable<(DateTime Date, double? Value)> source) =>
        source
            .Where(x => x.Value.HasValue)
            .Select(x => new IndicatorPoint
            {
                OpenTime = new DateTimeOffset(DateTime.SpecifyKind(x.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                Value = (decimal)x.Value!.Value
            })
            .ToList();
}
