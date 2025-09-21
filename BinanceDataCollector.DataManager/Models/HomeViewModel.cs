using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.DataManager.Models;

public class HomeViewModel
{
    /// <summary>
    /// Статус системы в целом.
    /// </summary>
    public string SystemStatus { get; set; } = "Unknown";

    /// <summary>
    /// Количество отслеживаемых в данный момент символов.
    /// </summary>
    public int TrackedSymbolsCount { get; set; }

    /// <summary>
    /// Самая последняя сделка, сохраненная в базе данных.
    /// </summary>
    public Trade? LastTrade { get; set; }

    /// <summary>
    /// Информация о серверах Hangfire (чтобы видеть, что они работают).
    /// </summary>
    public List<ServerDto> HangfireServers { get; set; } = new();
}

// Вспомогательный класс для отображения данных о серверах Hangfire
public class ServerDto
{
    public string Name { get; set; }
    public string Queues { get; set; }
    public int WorkerCount { get; set; }
    public DateTime Heartbeat { get; set; }
}