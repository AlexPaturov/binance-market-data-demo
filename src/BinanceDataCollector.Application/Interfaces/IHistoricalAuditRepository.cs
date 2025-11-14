using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Репозиторий для управления процессом глубокого исторического аудита данных.
/// </summary>
public interface IHistoricalAuditRepository
{
    /// <summary>
    /// Находит символы, для которых еще не создан аудит, и создает для них
    /// начальные записи в таблице вотермарок.
    /// </summary>
    Task InitializeAuditForNewSymbolsAsync();

    /// <summary>
    /// Получает пачку символов, которые нуждаются в проверке.
    /// Выбирает "ожидающие" или "сбойные" (если прошло достаточно времени).
    /// </summary>
    /// <param name="batchSize">Максимальное количество символов для возврата.</param>
    /// <param name="maxRetries">Максимальное количество попыток для "сбойных" записей.</param>
    /// <param name="retryInterval">Интервал, после которого можно повторить "сбойную" попытку.</param>
    Task<IEnumerable<HistoricalWatermark>> GetSymbolsToAuditAsync(int batchSize, int maxRetries, TimeSpan retryInterval);

    /// <summary>
    /// Обновляет состояние (вотермарку) для указанного символа.
    /// </summary>
    /// <param name="symbol">Символ для обновления.</param>
    /// <param name="newTradeId">Новое значение для LastChecked_TradeId.</param>
    /// <param name="newTimestamp">Новое значение для LastChecked_Timestamp.</param>
    /// <param name="newStatus">Новый статус ('Pending', 'Completed', 'Failed').</param>
    /// <param name="incrementRetryCount">Нужно ли увеличивать счетчик попыток.</param>
    Task UpdateWatermarkAsync(string symbol, long newTradeId, long newTimestamp, string newStatus, bool incrementRetryCount);
}
