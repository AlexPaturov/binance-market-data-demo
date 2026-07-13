using BinanceDataCollector.Application.Analytics.Models;

namespace BinanceDataCollector.Application.Interfaces;

public interface IChartRepository
{
    /// <summary>
    /// Последние <paramref name="limit"/> свечей таймфрейма, собранные из минутных.
    /// </summary>
    Task<List<ChartCandle>> GetCandlesAsync(string symbol, string timeframe, int limit);

    /// <summary>
    /// Свечи, начиная с бара <paramref name="sinceMs"/> включительно — для дозагрузки
    /// свежих данных без перекачивания всего графика. Последний бар может быть
    /// незакрытым: он продолжает набираться.
    /// </summary>
    Task<List<ChartCandle>> GetCandlesSinceAsync(string symbol, string timeframe, long sinceMs);

    /// <summary>
    /// CVD на конец каждого бара. Ряд кумулятивный, поэтому берётся последнее
    /// минутное значение внутри бара, а не сумма.
    /// </summary>
    Task<List<IndicatorPoint>> GetCvdAsync(string symbol, string timeframe, long fromMs, long toMs);
}
