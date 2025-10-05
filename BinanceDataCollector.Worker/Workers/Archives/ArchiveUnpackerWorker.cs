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
public class ArchiveUnpackerWorker
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

        _logger.LogInformation("Начинаю распаковку архива: {FileName}", zipFileName);
        await _notifier.SendStatusUpdateAsync(connectionId, $"Начинаю распаковку архива: {zipFileName}...");

        try
        {
            if (!File.Exists(zipFilePath))
            {
                _logger.LogWarning("ZIP-файл {FileName} не найден для распаковки.", zipFileName);
                await _notifier.SendStatusUpdateAsync(connectionId, $"ZIP-файл {zipFileName} не найден для распаковки.");
                return;
            }

            // 1. Проверка целостности
            using (var zipToTest = ZipFile.OpenRead(zipFilePath))
            {
                if (zipToTest.Entries.Count == 0 || zipToTest.Entries.All(e => e.Length == 0))
                {
                    throw new InvalidDataException("Архив пуст или не содержит данных.");
                }
            }
            _logger.LogDebug("Проверка целостности для {FileName} пройдена.", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Проверка целостности для {zipFileName} пройдена.");

            // 2. Распаковка (с перезаписью, если папка уже существует)
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
                _logger.LogDebug("Существующая директория {Directory} удалена перед распаковкой.", destinationDirectory);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Существующая директория {destinationDirectory} удалена перед распаковкой.");
            }

            ZipFile.ExtractToDirectory(zipFilePath, destinationDirectory);
            _logger.LogInformation("Архив {FileName} успешно распакован в {Directory}.", zipFileName, destinationDirectory);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Архив {zipFileName} успешно распакован в {destinationDirectory}.");

            // 3. Запуск следующего этапа - импорта
            var csvFile = Directory.GetFiles(destinationDirectory, "*.csv").FirstOrDefault();
            if (csvFile == null)
            {
                throw new FileNotFoundException("CSV-файл не найден в распакованном архиве.");
            }

            // Ставим задачу на импорт
            _backgroundJobClient.Enqueue<CsvImportWorker>(
                worker => worker.ImportFromCsvAsync(csvFile, connectionId, JobCancellationToken.Null)
            );

            _logger.LogInformation("Задача на импорт файла {CsvFile} поставлена в очередь.", Path.GetFileName(csvFile));
            await _notifier.SendStatusUpdateAsync(connectionId, $"Задача на импорт файла {Path.GetFileName(csvFile)} поставлена в очередь.");

            // 4. (Опционально) Удаление ZIP-файла после успешной распаковки
            File.Delete(zipFilePath);
            _logger.LogInformation("Исходный ZIP-файл {FileName} удален.", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Исходный ZIP-файл {zipFileName} удален.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при распаковке архива {FileName}", zipFileName);
            await _notifier.SendStatusUpdateAsync(connectionId, $"<b style='color:red;'>Ошибка</b> при распаковке архива  {zipFileName}: {ex.Message}");
            // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            throw;
        }

        return;
    }

}
