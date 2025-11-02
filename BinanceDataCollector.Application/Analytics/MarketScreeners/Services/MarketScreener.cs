using Binance.Net.Clients;
using BinanceDataCollector.Application.Analytics.MarketScreeners;
using BinanceDataCollector.Application.Analytics.MarketScreeners.Models;

namespace BinanceDataCollector.Application.Analytics.MarketScreeners.Services;

// Создадим небольшой класс для удобного хранения результата


public class MarketScreener
{
    private readonly BinanceRestClient _restClient;

    public MarketScreener()
    {
        _restClient = new BinanceRestClient();
    }

    public async Task<List<InterestingPair>> FindTopPairsAsync(
        int topN = 40,
        decimal minQuoteVolumeInMillion = 10m)
    {
        Console.WriteLine("Получаем 24-часовую статистику по всем парам...");

        // 1. Получаем статистику одним запросом
        var tickerResult = await _restClient.SpotApi.ExchangeData.GetTickersAsync();

        if (!tickerResult.Success)
        {
            Console.WriteLine($"Ошибка: {tickerResult.Error?.Message}");
            return new List<InterestingPair>();
        }

        Console.WriteLine($"Найдено {tickerResult.Data.Count()} тикеров. Начинаем фильтрацию...");

        var minVolume = minQuoteVolumeInMillion * 1_000_000;

        // 2. Фильтруем и ранжируем
        var interestingPairs = tickerResult.Data
            .Where(t => t.Symbol.EndsWith("USDT"))                                              // а) Выбираем только USDT пары
            .Where(t => t.QuoteVolume > minVolume)                                              // б) Этап 1: ОТСЕИВАЕМ по минимальному объему (например, 10 млн USDT)
            .Select(t => new InterestingPair(t.Symbol, t.QuoteVolume, t.PriceChangePercent))    // в) Преобразуем в наш удобный формат
            .OrderByDescending(p => p.QuoteVolume)                                              // г) Этап 2: СОРТИРУЕМ оставшихся по объему (самые ликвидные вверху)
            .Take(topN)                                                                         // д) Берем ТОП-N пар 
            .ToList();

        return interestingPairs;
    }
}
