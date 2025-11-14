namespace BinanceDataCollector.Application.Archives.Models;

/// <summary>
/// Downloaded archive information
/// </summary>
public class ArchivedFileInfo
{
    public string FileName { get; set; }
    public string Symbol { get; set; } // Например, "PUMPUSDT"
    public DateOnly Date { get; set; }  // Например, 2025-09-17
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "Downloaded"; // Пока что просто "скачан"
}