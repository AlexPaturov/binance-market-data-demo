namespace BinanceDataCollector.DataManager.Models;

/// <summary>
/// Для загрузки архивов по всему списку активных символов
/// </summary>
public class DownloadArchivesRequest
{
    public string? Symbol { get; set; }
    public bool DownloadAll { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
