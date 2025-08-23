using BinanceDataCollector.Domain.Entities;
using Skender.Stock.Indicators;

namespace BinanceDataCollector.Application.Analytics;

/// <summary>
/// Сервис для расчета технических индикаторов на основе исторических данных.
/// Использует библиотеку Skender.Stock.Indicators.
/// </summary>
public class IndicatorService
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
        // 1. Конвертируем наши свечи в формат Quote
        var quotes = ConvertToQuotes(klines).ToList(); // Материализуем, чтобы не перечислять много раз
        if (!quotes.Any())
        {
            return Enumerable.Empty<FeatureData>();
        }

        // 2. Создаем "карту" результатов, где ключ - это дата свечи
        var featureMap = quotes.ToDictionary(
            quote => quote.Date,
            quote => new FeatureData
            {
                Symbol = symbol,
                OpenTime = ((DateTimeOffset)quote.Date).ToUnixTimeMilliseconds()
            });

        // 3. Рассчитываем каждый индикатор и добавляем его в нашу "карту"

        // 1. RSI (Relative Strength Index)
        var rsiResult = quotes.GetRsi(14);
        foreach (var rsi in rsiResult.Where(r => r.Rsi.HasValue))
        {
            if (featureMap.TryGetValue(rsi.Date, out var feature))
            {
                feature.Rsi14 = (decimal?)rsi.Rsi;
            }
        }

        // 2. MACD (Moving Average Convergence Divergence)
        var macdResult = quotes.GetMacd(12, 26, 9);
        foreach (var macd in macdResult.Where(m => m.Signal.HasValue))
        {
            if (featureMap.TryGetValue(macd.Date, out var feature))
            {
                feature.MacdSignal = (decimal?)macd.Signal;
                feature.MacdHist = (decimal?)macd.Histogram;
            }
        }

        // 3. 2-Year Moving Average (SMA)
        var ma2yResult = quotes.GetSma(Ma2yPeriods);
        foreach (var ma in ma2yResult.Where(m => m.Sma.HasValue))
        {
            if (featureMap.TryGetValue(ma.Date, out var feature))
            {
                feature.Ma1051200 = (decimal?)ma.Sma;
            }
        }

        // 4. 200-Week Moving Average (SMA)
        var ma200wResult = quotes.GetSma(Ma200wPeriods);
        foreach (var ma in ma200wResult.Where(m => m.Sma.HasValue))
        {
            if (featureMap.TryGetValue(ma.Date, out var feature))
            {
                feature.Ma201600 = (decimal?)ma.Sma;
            }
        }

        // (Здесь в будущем можно добавить расчеты других индикаторов по тому же принципу)

        // 4. Возвращаем значения из нашей "карты", отсортированные по времени
        return featureMap.Values.OrderBy(f => f.OpenTime);
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
