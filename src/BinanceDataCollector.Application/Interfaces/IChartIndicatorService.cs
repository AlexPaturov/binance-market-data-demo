using BinanceDataCollector.Application.Analytics.Models;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Считает индикаторы для графика на свечах выбранного таймфрейма.
///
/// Отдельно от <see cref="IIndicatorService"/> намеренно: тот считает фичи для ML
/// строго по минутным свечам с фиксированными периодами (включая SMA на 1M+ баров).
/// Графику нужны те же индикаторы, но на 15м/1ч/4ч/1д/1нед и с настраиваемыми периодами.
/// </summary>
public interface IChartIndicatorService
{
    ChartIndicators Calculate(IReadOnlyList<ChartCandle> candles, IndicatorSettings settings);
}
