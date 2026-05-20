using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using Hangfire;
using Serilog.Context;

namespace BinanceDataCollector.Worker.Workers.Archives;

[Queue("archive_import")]               // Выполняем в низкоприоритетной очереди
//[DisableConcurrentExecution(10 * 60)]   // Таймаут на скачивание одного файла - 10 минут
public class ArchiveDownloaderWorker : IArchiveDownloaderWorker
{
    private readonly IArchiveService _archiveService; // Предполагаем, что он уже есть
    private readonly ILogger<ArchiveDownloaderWorker> _logger;
    private readonly IStatusNotifier _notifier;

    // Путь, куда будем сохранять архивы. Можно вынести в appsettings.json
    private readonly string _downloadPath;

    public ArchiveDownloaderWorker(
        IArchiveService archiveService, 
        ILogger<ArchiveDownloaderWorker> logger,
        IStatusNotifier notifier,
        IPathProvider pathProvider)
    {
        _archiveService = archiveService;
        _logger = logger;
        _downloadPath = pathProvider.GetTradeArchivesPath(); 
        _notifier = notifier;
    }

    /// <summary>
    /// Скачивает ОДИН архив и сохраняет его на диск.
    /// </summary>
    public async Task DownloadArchiveAsync(Guid requestId, string connectionId, string symbol, DateOnly date)
    {
        var fileName = $"{symbol}-trades-{date:yyyy-MM-dd}.zip";
        Directory.CreateDirectory(_downloadPath); // создаём директорию
        var filePath = Path.Combine(_downloadPath, fileName);
        FileStream? fileStream = null;
        
        using (LogContext.PushProperty("RequestId", requestId)) // Используем requestId для ЛОГИРОВАНИЯ
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > 0)
                    {
                        _logger.LogInformation("Archive {FileName} already exists and is non-empty. Download skipped.", fileName);
                        await _notifier.SendStatusUpdateAsync(connectionId, $"Archive {fileName} already exists and is non-empty. Download skipped.");
                        return;
                    }
                    _logger.LogWarning("Found empty artifact file {FileName}. Attempting to overwrite.", fileName);
                    await _notifier.SendStatusUpdateAsync(connectionId, $"Found empty artifact file {fileName}. Attempting to overwrite.");
                }

                Directory.CreateDirectory(_downloadPath);
                fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

                bool success = await _archiveService.DownloadArchiveToStreamAsync(symbol, date, fileStream, CancellationToken.None);

                // --- ВАЖНО: Закрываем поток ДО проверки размера ---
                // Это гарантирует, что все буферы сброшены на диск и размер файла финальный.
                await fileStream.DisposeAsync();
                fileStream = null; // Обнуляем, чтобы finally его снова не закрыл.

                if (success)
                {
                    // --- НОВАЯ ПРОВЕРКА НА РАЗМЕР ---
                    var downloadedFileInfo = new FileInfo(filePath);
                    if (downloadedFileInfo.Length == 0)
                    {
                        _logger.LogWarning("Downloaded archive {FileName} is empty. Deleting.", fileName);
                        await _notifier.SendStatusUpdateAsync(connectionId, $"Downloaded archive {fileName} is empty. Deleting.");
                        downloadedFileInfo.Delete();
                    }
                    else
                    {
                        _logger.LogInformation("Archive {FileName} downloaded and saved successfully ({Size} KB).", fileName, (downloadedFileInfo.Length / 1024.0).ToString("F2"));
                        await _notifier.SendStatusUpdateAsync(connectionId, $"Archive {fileName} downloaded and saved successfully ({(downloadedFileInfo.Length / 1024.0).ToString("F2")} KB).");
                    }
                    // ------------------------------------
                }
                else
                {
                    // Если !success (была ошибка 404), то файл, созданный FileStream,
                    // остался пустым. Удалим его.
                    _logger.LogDebug("Deleting empty artifact file {FileName} after failed download (404).", fileName);
                    await _notifier.SendStatusUpdateAsync(connectionId, $"Deleting empty artifact file {fileName} after failed download (404).");
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error downloading archive for {Symbol} on {Date}", symbol, date);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Deleting empty artifact file {fileName} after failed download (404).");

                // Очистка: удаляем потенциально недокачанный или пустой файл
                if (File.Exists(filePath))
                {
                    try
                    {
                        await fileStream.DisposeAsync();
                        File.Delete(filePath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Failed to delete artifact file {FileName}", fileName);
                    }
                }

                throw;
            }
            finally
            {
                // Если fileStream не был обнулен, закрываем его
                if (fileStream != null)
                {
                    await fileStream.DisposeAsync();
                }
            }
        }
    }
}
