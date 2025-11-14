namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Хранит состояние процесса исторического аудита для одного символа.
/// </summary>
public record HistoricalWatermark
{
    public required string Symbol { get; init; }
    public long LastChecked_TradeId { get; init; }
    public long LastChecked_Timestamp { get; init; }
    public required string Status { get; init; }
    public int RetryCount { get; init; }
    public DateTime? LastAttempt_UTC { get; init; }
}
