namespace BinanceDataCollector.Application.Common;

// Список приоритезации права доступа к ресурсу
public enum ApiRequestPriority
{
    Realtime,           // Самый высокий приоритет. Быстрые, критичные мета-запросы.
    Live,               // Сбор "живых" данных, важнее, чем аудит.
    QuickAudit,         // Быстрый, оперативный аудит.
    HistoricalAudit     // Низкоприоритетный, фоновый аудит.
}
