using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Worker.Workers.Archives;

[Queue("archive_import")]               // Выполняем в низкоприоритетной очереди
//[DisableConcurrentExecution(10 * 60)]   // Таймаут на скачивание одного файла - 10 минут
public class ArchiveDownloaderWorker
{
    private readonly IArchiveService _archiveService; // Предполагаем, что он уже есть
    private readonly ILogger<ArchiveDownloaderWorker> _logger;
#pragma warning disable S1450 // Private fields only used as local variables in methods should become local variables
    private readonly IOptions<ArchivesSettings> _options;
#pragma warning restore S1450 // Private fields only used as local variables in methods should become local variables

    // Путь, куда будем сохранять архивы. Можно вынести в appsettings.json
    private readonly string _downloadPath;

    public ArchiveDownloaderWorker(
        IArchiveService archiveService, 
        ILogger<ArchiveDownloaderWorker> logger,
        IOptions<ArchivesSettings> options)
    {
        _archiveService = archiveService;
        _logger = logger;
        _options = options;
        _downloadPath = _options.Value.TradeArcihvesPath;
    }

    /// <summary>
    /// Скачивает ОДИН архив и сохраняет его на диск.
    /// </summary>
    public async Task DownloadArchiveAsync(string symbol, DateOnly date)
    {
        var fileName = $"{symbol}-trades-{date:yyyy-MM-dd}.zip";
        var filePath = Path.Combine(_downloadPath, fileName);
        FileStream? fileStream = null;

        try
        {
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 0)
                {
                    _logger.LogInformation("Архив {FileName} уже существует и не пустой. Скачивание пропущено.", fileName);
                    return;
                }
                _logger.LogWarning("Найден пустой файл-артефакт {FileName}. Попытка перезаписать.", fileName);
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
                    _logger.LogWarning("Скачанный архив {FileName} оказался пустым. Удаляем.", fileName);
                    downloadedFileInfo.Delete();
                }
                else
                {
                    _logger.LogInformation("Архив {FileName} успешно скачан и сохранен ({Size} KB).", fileName, (downloadedFileInfo.Length / 1024.0).ToString("F2"));
                }
                // ------------------------------------
            }
            else
            {
                // Если !success (была ошибка 404), то файл, созданный FileStream,
                // остался пустым. Удалим его.
                _logger.LogDebug("Удаляем пустой файл-артефакт {FileName} после неудачного скачивания (404).", fileName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при скачивании архива для {Symbol} за {Date}", symbol, date);

            // Очистка: удаляем потенциально недокачанный или пустой файл
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { /* Игнорируем ошибки очистки */ }
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
