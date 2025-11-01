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
    private const int Ma2yPeriods = 1051200;  // 2-year Moving Average (в минутных свечах)
    private const int Ma200wPeriods = 2016000; // 200-week Moving Average (в минутных свечах)

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
        var ma2yResult = quotes.GetSma(Ma2yPeriods);
        var ma200wResult = quotes.GetSma(Ma200wPeriods);

        // --- НАДЕЖНОЕ ОБЪЕДИНЕНИЕ РЕЗУЛЬТАТОВ ---
        // Мы итерируемся по ИСХОДНОМУ списку свечей (quotes) и для каждой
        // ищем соответствующий результат в коллекциях индикаторов.

        var features = quotes.Select(q => {
            var rsi = rsiResult.FirstOrDefault(r => r.Date == q.Date);                     // Ищем результат RSI для текущей даты
            var macd = macdResult.FirstOrDefault(m => m.Date == q.Date);                 // Ищем результат MACD для текущей даты
            var ma2y = ma2yResult.FirstOrDefault(m => m.Date == q.Date);        // Ищем результат MA 2Y для текущей даты
            var ma200w = ma200wResult.FirstOrDefault(m => m.Date == q.Date);    // Ищем результат MA 200W для текущей даты

            return new FeatureData
            {
                Symbol = symbol,
                OpenTime = ((DateTimeOffset)q.Date).ToUnixTimeMilliseconds(),
                Rsi14 = (decimal?)rsi?.Rsi,
                MacdSignal = (decimal?)macd?.Signal,
                MacdHist = (decimal?)macd?.Histogram,
                Ma1051200 = (decimal?)ma2y?.Sma,
                Ma201600 = (decimal?)ma200w?.Sma
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
