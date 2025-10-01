namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Определяет контракт для сервиса, отвечающего за отправку уведомлений о статусе
/// фоновых процессов в реальном времени.
/// Этот интерфейс является абстракцией, скрывающей конкретную технологию доставки
/// (например, SignalR, RabbitMQ, Redis Pub/Sub).
/// </summary>
public interface IStatusNotifier
{
    /// <summary>
    /// Асинхронно отправляет сообщение о статусе конкретному клиенту.
    /// </summary>
    /// <param name="connectionId">
    /// Уникальный идентификатор клиента или группы клиентов, которым предназначено сообщение.
    /// В контексте SignalR это может быть `Context.ConnectionId`.
    /// </param>
    /// <param name="message">Текстовое сообщение о статусе, которое будет отправлено клиенту.</param>
    /// <returns>Объект <see cref="Task"/>, представляющий асинхронную операцию.</returns>
    Task SendStatusUpdateAsync(string connectionId, string message);
}
