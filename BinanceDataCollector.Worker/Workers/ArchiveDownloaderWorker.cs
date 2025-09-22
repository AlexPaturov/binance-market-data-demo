using BinanceDataCollector.Application.Archives;
using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Worker.Workers;

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
        try
        {
            // Убеждаемся, что директория существует
            Directory.CreateDirectory(_downloadPath);

            var fileName = $"{symbol}-trades-{date:yyyy-MM-dd}.zip";
            var filePath = Path.Combine(_downloadPath, fileName);

            if (File.Exists(filePath))
            {
                _logger.LogInformation("Архив {FileName} уже существует. Скачивание пропущено.", fileName);
                return;
            }

            // В IArchiveService нужен новый метод, который возвращает Stream
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await _archiveService.DownloadArchiveToStreamAsync(symbol, date, fileStream, CancellationToken.None);

            _logger.LogInformation("Архив {FileName} успешно скачан и сохранен.", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скачивании архива для {Symbol} за {Date}", symbol, date);
            throw; // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
        }
    }
}
