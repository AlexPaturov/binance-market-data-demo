namespace BinanceDataCollector.Domain.DTOs;

/// <summary>
/// Настройки для работы с архиввами
/// </summary>
public class ArchivesSettings
{
    public string TradeArcihvesPath { get; set; } = string.Empty;
    public string OhlcvArchivesPath { get; set; } = string.Empty;
}
