using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Skender.Stock.Indicators;

namespace BinanceDataCollector.Application.Analytics;

/// <summary>
/// Сервис для расчета технических индикаторов на основе исторических данных.
/// Использует библиотеку Skender.Stock.Indicators.
/// </summary>
public class IndicatorService : IIndicatorService
{
    // --- Константы для периодов индикаторов ---
    private const int RsiPeriods = 14;
    private const int MacdFastPeriods = 12;
    private const int MacdSlowPeriods = 26;
    private const int MacdSignalPeriods = 9;

    /// <summary>
    /// Главный метод, который принимает историю свечей и рассчитывает все необходимые индикаторы.
    /// </summary>
    /// <param name="symbol">Символ, для которого производятся расчеты.</param>
    /// <param name="klines">Коллекция свечей (OHLCV). Должна содержать "период прогрева".</param>
    /// <returns>Коллекция объектов FeatureData с рассчитанными значениями.</returns>
    public IEnumerable<FeatureData> CalculateAll(string symbol, IEnumerable<Ohlcv> klines)
    {
        var quotes = ConvertToQuotes(klines).ToList();
        if (!quotes.Any())
        {
            return Enumerable.Empty<FeatureData>();
        }

        // Рассчитываем все индикаторы. Библиотека сама обрабатывает "прогрев".
        var rsiResult = quotes.GetRsi(RsiPeriods);
        var macdResult = quotes.GetMacd(MacdFastPeriods, MacdSlowPeriods, MacdSignalPeriods);

        // --- НАДЕЖНОЕ ОБЪЕДИНЕНИЕ РЕЗУЛЬТАТОВ ---
        // Итерируемся по исходному списку свечей и сопоставляем результаты индикаторов
        // по дате через словари: пачки бывают в тысячи свечей, линейный поиск на каждую
        // превращался бы в квадрат.
        var rsiByDate = rsiResult.ToDictionary(r => r.Date);
        var macdByDate = macdResult.ToDictionary(m => m.Date);

        var features = quotes.Select(q => {
            rsiByDate.TryGetValue(q.Date, out var rsi);
            macdByDate.TryGetValue(q.Date, out var macd);

            return new FeatureData
            {
                Symbol = symbol,
                OpenTime = ((DateTimeOffset)q.Date).ToUnixTimeMilliseconds(),
                Rsi14 = (decimal?)rsi?.Rsi,
                MacdSignal = (decimal?)macd?.Signal,
                MacdHist = (decimal?)macd?.Histogram
            };
        });

        return features;
    }

    /// <summary>
    /// Вспомогательный метод для конвертации нашей модели Ohlcv в Quote.
    /// </summary>
    private IEnumerable<Quote> ConvertToQuotes(IEnumerable<Ohlcv> klines)
    {
        return klines.Select(k => new Quote
        {
            Date = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime,
            Open = k.OpenPrice,
            High = k.HighPrice,
            Low = k.LowPrice,
            Close = k.ClosePrice,
            Volume = k.Volume
        }).OrderBy(q => q.Date); // Библиотека требует отсортированные по дате данные
    }
}
