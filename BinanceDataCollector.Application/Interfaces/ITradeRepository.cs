using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(long tradeId, string symbol);
    Task<IEnumerable<Trade>> GetLatestTradesAsync(string symbol, int count);

    // Самый важный метод для быстрой вставки множества записей
    Task BulkInsertAsync(IEnumerable<Trade> trades);

    /// <summary>
    /// Находит Unix-время (ms) последней сохраненной сделки для указанного символа.
    /// Для заполнения "дыр".
    /// </summary>
    /// <returns>Время последней сделки или null, если данных нет.</returns>
    Task<long?> GetLastTradeTimeAsync(string symbol);

    /// <summary>
    /// Агрегирует тиковые данные в свечи
    /// </summary>
    /// <returns></returns>
    Task ExecuteAggregationAsync();

    // на удаление
    /// <summary>
    /// Находит максимальный TradeId для указанного символа.
    /// </summary>
    Task<long?> GetLastTradeIdAsync(string symbol);

    Task<List<DataGap>> GetGapsForSymbolDayAsync(string symbol);
}
