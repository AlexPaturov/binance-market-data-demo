using System.IO.Compression;
using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers.Archives;

/// <summary>
/// Принять на вход имя скачанного ZIP-файла.
/// Проверить его на "вшивость" (что он не пустой и не битый).
/// Распаковать CSV-файл в отдельную подпапку.
/// После успешной распаковки, поставить в очередь Hangfire задачу для следующего эта-па — импорта (CsvImportWorker).
/// (Опционально) Удалить исходный ZIP-файл, чтобы не занимать место.
/// </summary>
public class ArchiveUnpackerWorker : IArchiveUnpackerWorker
{
    private readonly ILogger<ArchiveUnpackerWorker> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IStatusNotifier _notifier;
    private readonly string _downloadPath;
    private readonly string _destinationPath;

    public ArchiveUnpackerWorker(
        ILogger<ArchiveUnpackerWorker> logger,
        IPathProvider pathProvider,
        IBackgroundJobClient backgroundJobClient,
        IStatusNotifier notifier)
    {
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _notifier = notifier;
        _downloadPath = pathProvider.GetTradeArchivesPath();
        _destinationPath = pathProvider.GetTradeUnpackedPath();
    }

    /// <summary>
    /// Проверяет, распаковывает архив и ставит задачу на импорт.
    /// </summary>
    public async Task UnpackArchiveAsync(string zipFileName, string connectionId)
    {
        var zipFilePath = Path.Combine(_downloadPath, zipFileName);
        var destinationDirectory = Path.Combine(_destinationPath, Path.GetFileNameWithoutExtension(zipFileName));

        _logger.LogInformation("Starting archive extraction: {FileName}", zipFileName);
        await _notifier.SendStatusUpdateAsync(connectionId, $"Starting archive extraction: {zipFileName}...");

        try
        {
            if (!File.Exists(zipFilePath))
            {
                _logger.LogWarning("ZIP file {FileName} not found for extraction.", zipFileName);
                await _notifier.SendStatusUpdateAsync(connectionId, $"ZIP file {zipFileName} not found for extraction.");
                return;
            }

            // 1. Проверка целостности
            using (var zipToTest = ZipFile.OpenRead(zipFilePath))
            {
                if (zipToTest.Entries.Count == 0 || zipToTest.Entries.All(e => e.Length == 0))
                {
                    throw new InvalidDataException("Archive is empty or contains no data.");
                }
            }
            _logger.LogDebug("Integrity check passed for {FileName}.", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Integrity check passed for {zipFileName}.");

            // 2. Распаковка (с перезаписью, если папка уже существует)
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
                _logger.LogDebug("Existing directory {Directory} deleted before extraction.", destinationDirectory);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Existing directory {destinationDirectory} deleted before extraction.");
            }

            ZipFile.ExtractToDirectory(zipFilePath, destinationDirectory);
            _logger.LogInformation("Archive {FileName} extracted successfully to {Directory}.", zipFileName, destinationDirectory);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Archive {zipFileName} extracted successfully to {destinationDirectory}.");

            // 3. Запуск следующего этапа - импорта
            var csvFile = Directory.GetFiles(destinationDirectory, "*.csv").FirstOrDefault();
            if (csvFile == null)
            {
                throw new FileNotFoundException("CSV file not found in extracted archive.");
            }

            // Ставим задачу на импорт
            _backgroundJobClient.Enqueue<CsvImportWorker>(
                worker => worker.ImportFromCsvAsync(csvFile, connectionId, JobCancellationToken.Null)
            );

            _logger.LogInformation("Import job for file {CsvFile} queued.", Path.GetFileName(csvFile));
            await _notifier.SendStatusUpdateAsync(connectionId, $"Import job for file {Path.GetFileName(csvFile)} queued.");

            // 4. (Опционально) Удаление ZIP-файла после успешной распаковки
            File.Delete(zipFilePath);
            _logger.LogInformation("Source ZIP file {FileName} deleted.", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Source ZIP file {zipFileName} deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting archive {FileName}", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"<b style='color:red;'>Error</b> extracting archive {zipFileName}: {ex.Message}");
            // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            throw;
        }

        return;
    }

}
