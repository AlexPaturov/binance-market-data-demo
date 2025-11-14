namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Представляет состояние (вотермарку) для долгоживущего фонового процесса.
/// </summary>
/// <param name="ProcessName">Уникальное имя процесса.</param>
/// <param name="LastProcessedTimestamp">Последняя обработанная временная метка (Unix ms).</param>
/// <param name="Status">Текущий статус процесса.</param>
public record ProcessWatermark(string ProcessName, long LastProcessedTimestamp, string Status);
