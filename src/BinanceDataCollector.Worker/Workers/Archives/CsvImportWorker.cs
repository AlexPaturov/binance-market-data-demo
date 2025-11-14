using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Hangfire;
using System.Text.RegularExpressions;
using static Hangfire.Storage.JobStorageFeatures;

namespace BinanceDataCollector.Worker.Workers.Archives;

/// <summary>
/// Принять на вход путь к конкретному CSV-файлу.
/// Открыть и прочитать этот файл.
/// Распарсить каждую строку в объект Trade.
/// Накопить "пачку" (batch) объектов Trade.
/// Периодически сбрасывать эту пачку в базу данных с помощью BulkInsertAsync.
/// (Опционально) После успешного импорта удалить папку с CSV.
/// </summary>


[Queue("archive_import")]
// Этот атрибут очень важен! Он предотвратит одновременную массовую вставку
// данных в БД из нескольких файлов, что может вызвать блокировки или тайм-ауты.
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)] // Один час на импорт одного файла
public class CsvImportWorker
{
    private readonly ILogger<CsvImportWorker> _logger;
    private readonly ITradeRepository _tradeRepo;
    private readonly IArchiveService _archiveService;
    private readonly IStatusNotifier _notifier;
    private const int BatchSize = 10000; // Увеличим пачку для импорта из файла

    public CsvImportWorker(
        ILogger<CsvImportWorker> logger,
        ITradeRepository tradeRepo,
        IArchiveService archiveService,
        IStatusNotifier notifier)
    {
        _logger = logger;
        _tradeRepo = tradeRepo;
        _archiveService = archiveService;
        _notifier = notifier;
    }

    /// <summary>
    /// Импортирует данные из одного CSV-файла в базу данных.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task ImportFromCsvAsync(string csvFilePath, string connectionId, IJobCancellationToken cancellationToken)
    {
        _logger.LogInformation("Начинаю импорт из CSV-файла: {FileName}", Path.GetFileName(csvFilePath));
        await _notifier.SendStatusUpdateAsync(connectionId, $"Начинаю импорт из CSV-файла: {Path.GetFileName(csvFilePath)}...");

        if (!File.Exists(csvFilePath))
        {
            _logger.LogError("CSV-файл {Path} не найден для импорта.", csvFilePath);
            await _notifier.SendStatusUpdateAsync(connectionId, $"CSV-файл {Path.GetFileName(csvFilePath)} не найден для импорта");
            throw new FileNotFoundException("CSV file not found for import.", csvFilePath);
        }

        var (symbol, date) = ParseDetailsFromCsvPath(csvFilePath);
        if (symbol == "UNKNOWN")
        {
            _logger.LogError("Не удалось определить символ и дату из пути: {Path}", csvFilePath);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Не удалось определить символ и дату из пути: {csvFilePath}");
            throw new ArgumentException("Could not parse symbol and date from file path.", csvFilePath);
        }

        var batch = new List<Trade>(BatchSize);
        long totalInserted = 0;

        try
        {
            await using var fileStream = File.OpenRead(csvFilePath);

            await foreach (var trade in _archiveService.ParseTradesFromCsvStreamAsync(fileStream, symbol, cancellationToken.ShutdownToken))
            {
                batch.Add(trade);

                if (batch.Count >= BatchSize)
                {
                    await _tradeRepo.BulkInsertAsync(batch);
                    totalInserted += batch.Count;
                    _logger.LogDebug("[{Symbol}] Вставлена пачка из {Count} сделок...", symbol, batch.Count);
                    await _notifier.SendStatusUpdateAsync(connectionId, $"[{symbol}] Вставлена пачка из {batch.Count} сделок...");
                    batch.Clear();
                }
            }

            if (batch.Any())
            {
                await _tradeRepo.BulkInsertAsync(batch);
                totalInserted += batch.Count;
            }

            _logger.LogInformation("Импорт из файла {FileName} успешно завершен. Всего вставлено {TotalCount} сделок.", Path.GetFileName(csvFilePath), totalInserted);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Импорт из файла {Path.GetFileName(csvFilePath)} успешно завершен. Всего вставлено {totalInserted} сделок.");

            // (Опционально) Очистка после успешного импорта
            var parentDirectory = Directory.GetParent(csvFilePath)?.FullName;
            if (parentDirectory != null && Directory.Exists(parentDirectory))
            {
                Directory.Delete(parentDirectory, true);
                _logger.LogInformation("Папка {Directory} с обработанным CSV удалена.", parentDirectory);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Папка {parentDirectory} с обработанным CSV удалена.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при импорте из файла {FileName}", Path.GetFileName(csvFilePath));
            await _notifier.SendStatusUpdateAsync(connectionId, $"Ошибка при импорте из файла {Path.GetFileName(csvFilePath)}");
            throw; // Перевыбрасываем для Hangfire
        }
    }

    private (string Symbol, DateOnly Date) ParseDetailsFromCsvPath(string csvFilePath)
    {
        var directoryName = Path.GetFileName(Directory.GetParent(csvFilePath)?.FullName ?? string.Empty);
        var regex = new Regex(@"^(?<symbol>.+?)-trades-(?<date>\d{4}-\d{2}-\d{2})$");
        var match = regex.Match(directoryName);

        if (match.Success && DateOnly.TryParse(match.Groups["date"].Value, out var date))
        {
            return (match.Groups["symbol"].Value, date);
        }
        return ("UNKNOWN", DateOnly.MinValue);
    }
}
