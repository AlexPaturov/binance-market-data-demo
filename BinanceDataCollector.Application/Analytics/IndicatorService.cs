using BinanceDataCollector.Domain.Entities;
using Skender.Stock.Indicators;

namespace BinanceDataCollector.Application.Analytics;

/// <summary>
/// Сервис для расчета технических индикаторов на основе исторических данных.
/// Использует библиотеку Skender.Stock.Indicators.
/// </summary>
public class IndicatorService
{
    /// <summary>
    /// Главный метод, который принимает историю свечей и рассчитывает все необходимые индикаторы.
    /// </summary>
    /// <param name="symbol">Символ, для которого производятся расчеты.</param>
    /// <param name="klines">Коллекция свечей (OHLCV). Должна содержать "период прогрева".</param>
    /// <returns>Коллекция объектов FeatureData с рассчитанными значениями.</returns>
    public IEnumerable<FeatureData> CalculateAll(string symbol, IEnumerable<Ohlcv> klines)
    {
        // 1. Конвертируем наши доменные модели Ohlcv в формат Quote, понятный библиотеке.
        var quotes = ConvertToQuotes(klines);

        // 2. Рассчитываем все индикаторы.
        // Skender.Stock.Indicators очень оптимизирована и делает это эффективно.
        var rsi = quotes.GetRsi(14);
        var macd = quotes.GetMacd(12, 26, 9);
        var ma2y = quotes.GetSma(1051200); // 2-year Moving Average (минутные свечи)
        var ma200w = quotes.GetSma(2016000); // 200-week Moving Average (минутные свечи)

        // 3. Собираем все результаты в единую структуру.
        // Мы используем LINQ Join для объединения результатов по дате.
        var features = from quote in quotes
                       join rsiPoint in rsi on quote.Date equals rsiPoint.Date into rsiGroup
                       from rsiVal in rsiGroup.DefaultIfEmpty()
                       join macdPoint in macd on quote.Date equals macdPoint.Date into macdGroup
                       from macdVal in macdGroup.DefaultIfEmpty()
                       join ma2yPoint in ma2y on quote.Date equals ma2yPoint.Date into ma2yGroup
                       from ma2yVal in ma2yGroup.DefaultIfEmpty()
                       join ma200wPoint in ma200w on quote.Date equals ma200wPoint.Date into ma200wGroup
                       from ma200wVal in ma200wGroup.DefaultIfEmpty()
                       select new FeatureData
                       {
                           Symbol = symbol,
                           OpenTime = ((DateTimeOffset)quote.Date).ToUnixTimeMilliseconds(),
                           Rsi14 = (decimal?)rsiVal?.Rsi,
                           MacdSignal = (decimal?)macdVal?.Signal,
                           MacdHist = (decimal?)macdVal?.Histogram,
                           Ma1051200 = (decimal?)ma2yVal?.Sma,
                           Ma201600 = (decimal?)ma200wVal?.Sma
                           // CVD будет добавлен позже из другого источника
                       };

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
