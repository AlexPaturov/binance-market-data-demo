using Binance.Net.Clients;
using Binance.Net.Enums;
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

        // 2. Получаем статусы пар. У пары с остановленными торгами (status != TRADING)
        //    ticker/24hr продолжает отдавать замороженный объём, поэтому по одному
        //    объёму такую пару не отсечь — нужен exchangeInfo.
        var exchangeInfoResult = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync();

        if (!exchangeInfoResult.Success)
        {
            Console.WriteLine($"Ошибка: {exchangeInfoResult.Error?.Message}");
            return new List<InterestingPair>();
        }

        var tradingSymbols = exchangeInfoResult.Data.Symbols
            .Where(s => s.Status == SymbolStatus.Trading)
            .Select(s => s.Name)
            .ToHashSet();

        Console.WriteLine($"Найдено {tickerResult.Data.Count()} тикеров, из них торгуются {tradingSymbols.Count}. Начинаем фильтрацию...");

        var minVolume = minQuoteVolumeInMillion * 1_000_000;

        // 3. Фильтруем и ранжируем
        var interestingPairs = tickerResult.Data
            .Where(t => t.Symbol.EndsWith("USDT"))                                              // а) Выбираем только USDT пары
            .Where(t => tradingSymbols.Contains(t.Symbol))                                      // б) Только пары со статусом TRADING
            .Where(t => t.QuoteVolume > minVolume)                                              // в) Этап 1: ОТСЕИВАЕМ по минимальному объему (например, 10 млн USDT)
            .Select(t => new InterestingPair(t.Symbol, t.QuoteVolume, t.PriceChangePercent))    // г) Преобразуем в наш удобный формат
            .OrderByDescending(p => p.QuoteVolume)                                              // д) Этап 2: СОРТИРУЕМ оставшихся по объему (самые ликвидные вверху)
            .Take(topN)                                                                         // е) Берем ТОП-N пар
            .ToList();

        return interestingPairs;
    }
}
