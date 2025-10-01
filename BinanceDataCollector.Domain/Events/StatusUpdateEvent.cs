namespace BinanceDataCollector.Domain.Events;

/// <summary>
/// Представляет событие обновления статуса фоновой задачи,
/// предназначенное для передачи через брокер сообщений.
/// </summary>
public class StatusUpdateEvent
{
    /// <summary>
    /// Уникальный идентификатор соединения SignalR, которому предназначено сообщение.
    /// Позволяет адресно доставить уведомление конкретному клиенту (вкладке браузера).
    /// </summary>
    public string ConnectionId { get; set; }

    /// <summary>
    /// Текстовое сообщение о статусе. Может содержать простой текст или HTML-разметку.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// Временная метка (в UTC), когда событие было сгенерировано.
    /// По умолчанию устанавливается в момент создания объекта.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
