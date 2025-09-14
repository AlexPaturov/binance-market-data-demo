using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface ITradeRepository
{
    /// <summary>
    /// Находит одну сделку по ее уникальному составному ключу (Symbol и TradeId).
    /// </summary>
    /// <param name="tradeId">ID сделки.</param>
    /// <param name="symbol">Символ.</param>
    /// <returns>Объект сделки или null, если она не найдена.</returns>
    Task<Trade?> GetTradeByIdAsync(long tradeId, string symbol);
    
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


    /// <summary>
    /// Находит все пропуски в последовательности TradeId для указанного символа за последние 48 часов.
    /// </summary>
    Task<List<DataGap>> GetGapsForSymbolDayAsync(string symbol);

    /// <summary>
    /// Находит ID последней сделки, которая произошла ДО указанной временной метки.
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="timestampMs">Временная метка в Unix миллисекундах.</param>
    /// <returns>ID последней сделки или null, если таких сделок нет.</returns>
    Task<long?> GetLastTradeIdBeforeTimestampAsync(string symbol, long timestampMs);

    /// <summary>
    /// Получает самую последнюю сделку для указанного символа.
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <returns>Объект последней сделки или null, если данных нет.</returns>
    Task<Trade?> GetLastTradeAsync(string symbol);

    /// <summary>
    /// Получает УПОРЯДОЧЕННЫЙ список TradeId для указанного символа в заданном диапазоне ID.
    /// </summary>
    Task<IEnumerable<long>> GetTradeIdsInWindowAsync(string symbol, long startTradeId, long endTradeId);



}
