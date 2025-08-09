using Binance.Net.Clients;
using Binance.Net.Enums;

namespace BinanceDataCollector.Symbols;

public class SymbolFetcher
{
    public async Task<List<string>> GetSpotUsdtPairsAsync()
    {
        // Создаем REST-клиент. Ему не нужны ключи для публичных данных.
        var restClient = new BinanceRestClient();

        // 1. Запрашиваем информацию о бирже
        var exchangeInfoResult = await restClient.SpotApi.ExchangeData.GetExchangeInfoAsync();

        // 2. Проверяем, успешен ли был запрос
        if (!exchangeInfoResult.Success)
        {
            Console.WriteLine($"Ошибка при получении данных: {exchangeInfoResult.Error?.Message}");
            return new List<string>(); // Возвращаем пустой список в случае ошибки
        }

        // 3. Фильтруем и преобразуем данные с помощью LINQ
        var usdtSymbols = exchangeInfoResult.Data.Symbols
           .Where(s => s.Status == SymbolStatus.Trading)        // a) Выбираем только пары со статусом "TRADING"
           .Where(s => s.QuoteAsset == "USDT")                  // b) Выбираем только те, где котируемая валюта - USDT
           .Where(s => !s.Name.EndsWith("UP") && !s.Name.EndsWith("DOWN") && !s.Name.EndsWith("BEAR") && !s.Name.EndsWith("BULL")) // в) Исключаем фьючерсные пары UP/DOWN/BEAR/BULL
           .Select(s => s.Name)                                 // г) Выбираем только имена пар
           .ToList();

        return usdtSymbols;
    }
}
