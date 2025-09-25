using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;
using System.IO.Compression;

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
    private readonly string _downloadPath;
    private readonly string _destinationPath;

    public ArchiveUnpackerWorker(
        ILogger<ArchiveUnpackerWorker> logger,
        IOptions<ArchivesSettings> options,
        IBackgroundJobClient backgroundJobClient)
    {
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _downloadPath = options.Value.TradeArcihvesPath;
        _destinationPath = options.Value.CsvUnpackedPath;
    }

    /// <summary>
    /// Проверяет, распаковывает архив и ставит задачу на импорт.
    /// </summary>
    public Task UnpackArchiveAsync(string zipFileName)
    {
        var zipFilePath = Path.Combine(_downloadPath, zipFileName);
        var destinationDirectory = Path.Combine(_destinationPath, Path.GetFileNameWithoutExtension(zipFileName));

        _logger.LogInformation("Начинаю распаковку архива: {FileName}", zipFileName);

        try
        {
            if (!File.Exists(zipFilePath))
            {
                _logger.LogWarning("ZIP-файл {FileName} не найден для распаковки.", zipFileName);
                return Task.CompletedTask;
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

            // 2. Распаковка (с перезаписью, если папка уже существует)
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
                _logger.LogDebug("Существующая директория {Directory} удалена перед распаковкой.", destinationDirectory);
            }
            ZipFile.ExtractToDirectory(zipFilePath, destinationDirectory);
            _logger.LogInformation("Архив {FileName} успешно распакован в {Directory}.", zipFileName, destinationDirectory);

            // 3. Запуск следующего этапа - импорта
            var csvFile = Directory.GetFiles(destinationDirectory, "*.csv").FirstOrDefault();
            if (csvFile == null)
            {
                throw new FileNotFoundException("CSV-файл не найден в распакованном архиве.");
            }

            // Ставим задачу на импорт
            _backgroundJobClient.Enqueue<CsvImportWorker>(worker => worker.ImportFromCsvAsync(csvFile, JobCancellationToken.Null));
            _logger.LogInformation("Задача на импорт файла {CsvFile} поставлена в очередь.", Path.GetFileName(csvFile));

            // 4. (Опционально) Удаление ZIP-файла после успешной распаковки
            File.Delete(zipFilePath);
            _logger.LogInformation("Исходный ZIP-файл {FileName} удален.", zipFileName);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при распаковке архива {FileName}", zipFileName);
            // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            throw;
        }

        return Task.CompletedTask;
    }

}
