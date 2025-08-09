using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

// Интерфейс для получения данных с биржи
public interface IBinanceService
{

    // Подписка на поток сделок в реальном времени
    Task SubscribeToTradesAsync(string symbol, Func<Trade, Task> onTradeReceived);


    // автонагенерировано
    /// <summary>
    /// Получает последние сделки по указанной торговой паре.
    /// </summary>
    /// <param name="symbol">Торговая пара (например, "BTCUSDT").</param>
    /// <param name="limit">Максимальное количество сделок для получения.</param>
    /// <returns>Список последних сделок.</returns>
   // Task<IEnumerable<Trade>> GetLatestTradesAsync(string symbol, int limit = 100);
    /// <summary>
    /// Получает информацию о конкретной сделке по идентификатору.
    /// </summary>
    /// <param name="tradeId">Идентификатор сделки.</param>
    /// <param name="symbol">Торговая пара.</param>
    /// <returns>Информация о сделке или null, если сделка не найдена.</returns>
  //  Task<Trade?> GetTradeByIdAsync(long tradeId, string symbol);
}
