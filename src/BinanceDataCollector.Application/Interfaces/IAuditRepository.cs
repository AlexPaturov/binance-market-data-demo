using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAuditRepository
{
    /// <summary>
    /// Получает текущее состояние (вотермарку) для процесса агрегации свечей.
    /// </summary>
    /// <returns>Объект ProcessWatermark с последним состоянием.</returns>
    Task<ProcessWatermark?> GetAggregationWatermarkAsync();

    // <summary>
    /// Обновляет состояние (вотермарку) для процесса агрегации свечей.
    /// </summary>
    /// <param name="lastProcessedTimestamp">Новая временная метка, которую нужно сохранить.</param>
    /// <param name="status">Новый статус процесса ('Pending' или 'Completed').</param>
    Task UpdateAggregationWatermarkAsync(long lastProcessedTimestamp, string status);
}
