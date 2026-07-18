namespace BinanceDataCollector.Domain.DTOs;

/// <summary>
/// Настройки для работы с архиввами
/// </summary>
public class ArchivesSettings
{
    /// <summary>
    /// Абсолютный путь к папке скачанных архивов сделок. Используется
    /// OnlineArchiveImportWorker для импорта уже лежащих на диске zip.
    /// </summary>
    public string TradeArcihvesPath { get; set; } = string.Empty;

    /// <summary>
    /// Абсолютный корневой путь к директории с данными.
    /// Prod: /opt/bdc_data (монтируется как Docker volume).
    /// Dev: локальный путь, например C:\bdc_data.
    /// </summary>
    public string BasePath { get; set; } = "/opt/bdc_data";

    /// <summary>
    /// Имя корневой директории приложения в папке с данными (LocalApplicationData).
    /// </summary>
    public string RootDirectoryName { get; set; } = "BinanceDataCollector";
    
    /// <summary>
    /// Относительный путь для скачанных архивов сделок.
    /// </summary>
    public string TradeArchivesRelativePath { get; set; } = "Trades/Downloaded";
    
    /// <summary>
    /// Относительный путь для скачанных архивов сделок.
    /// </summary>
    public string TradeUnpackedRelativePath { get; set; } = "Trades/Unpacked";
    
    /// <summary>
    /// Относительный путь для скачанных архивов сделок.
    /// </summary>
    public string OhlcvArchivesRelativePath { get; set; } = "Ohlcv/Downloaded";
    
    /// <summary>
    /// Относительный путь для скачанных архивов сделок.
    /// </summary>
    public string OhlcvUnpackedRelativePath { get; set; } = "Ohlcv/Unpacked";
}
