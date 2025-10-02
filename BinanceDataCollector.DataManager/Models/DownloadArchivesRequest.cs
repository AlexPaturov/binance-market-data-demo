namespace BinanceDataCollector.DataManager.Models;

/// <summary>
/// Модель данных для запроса на скачивание архивов, поступающего от клиента.
/// </summary>
public class DownloadArchivesRequest
{
    /// <summary>
    /// Конкретный символ (торговая пара), для которого нужно скачать архивы.
    /// Используется, если свойство DownloadAll равно false.
    /// Может быть null, если выбран флаг "скачать для всех".
    /// </summary>
    public string? Symbol { get; set; }

    /// <summary>
    /// Флаг, указывающий, нужно ли скачивать архивы для всех активных символов.
    /// Если true, свойство Symbol игнорируется.
    /// </summary>
    public bool DownloadAll { get; set; }

    /// <summary>
    /// Начальная дата диапазона для скачивания архивов.
    /// </summary>
    public DateOnly StartDate { get; set; }

    /// <summary>
    /// Конечная дата диапазона для скачивания архивов (включительно).
    /// </summary>
    public DateOnly EndDate { get; set; }

    /// <summary>
    /// Уникальный идентификатор всей операции, инициированной пользователем.
    /// Генерируется на клиенте и используется для сквозной трассировки
    /// и логирования всех порожденных фоновых задач.
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// Уникальный идентификатор соединения SignalR.
    /// Используется для отправки уведомлений о статусе выполнения
    /// обратно в ту вкладку браузера, которая инициировала запрос.
    /// </summary>
    public string ConnectionId { get; set; }
}
