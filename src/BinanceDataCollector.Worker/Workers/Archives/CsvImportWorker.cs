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
    private readonly IImportBackpressure _backpressure;
    private const int BatchSize = 10000; // Увеличим пачку для импорта из файла

    public CsvImportWorker(
        ILogger<CsvImportWorker> logger,
        ITradeRepository tradeRepo,
        IArchiveService archiveService,
        IStatusNotifier notifier,
        IImportBackpressure backpressure)
    {
        _logger = logger;
        _tradeRepo = tradeRepo;
        _archiveService = archiveService;
        _notifier = notifier;
        _backpressure = backpressure;
    }

    /// <summary>
    /// Импортирует данные из одного CSV-файла в базу данных.
    /// </summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task ImportFromCsvAsync(string csvFilePath, string connectionId, IJobCancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting import from CSV file: {FileName}", Path.GetFileName(csvFilePath));
        await _notifier.SendStatusUpdateAsync(connectionId, $"Starting import from CSV file: {Path.GetFileName(csvFilePath)}...");

        if (!File.Exists(csvFilePath))
        {
            _logger.LogError("CSV file {Path} not found for import.", csvFilePath);
            await _notifier.SendStatusUpdateAsync(connectionId, $"CSV file {Path.GetFileName(csvFilePath)} not found for import");
            throw new FileNotFoundException("CSV file not found for import.", csvFilePath);
        }

        var (symbol, date) = ParseDetailsFromCsvPath(csvFilePath);
        if (symbol == "UNKNOWN")
        {
            _logger.LogError("Could not determine symbol and date from path: {Path}", csvFilePath);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Could not determine symbol and date from path: {csvFilePath}");
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
                    // Импорт работает на остатке ресурса: реалтайм-конвейер делит с ним
                    // IOPS диска, и когда лаг свечи выходит за порог — пачка ждёт.
                    await _backpressure.WaitForPipelineHeadroomAsync(cancellationToken.ShutdownToken);

                    await _tradeRepo.BulkInsertAsync(batch);
                    totalInserted += batch.Count;
                    _logger.LogDebug("[{Symbol}] Inserted batch of {Count} trades...", symbol, batch.Count);
                    await _notifier.SendStatusUpdateAsync(connectionId, $"[{symbol}] Inserted batch of {batch.Count} trades...");
                    batch.Clear();
                }
            }

            if (batch.Any())
            {
                await _backpressure.WaitForPipelineHeadroomAsync(cancellationToken.ShutdownToken);

                await _tradeRepo.BulkInsertAsync(batch);
                totalInserted += batch.Count;
            }

            _logger.LogInformation("Import from file {FileName} completed successfully. Total inserted: {TotalCount} trades.", Path.GetFileName(csvFilePath), totalInserted);
            await _notifier.SendStatusUpdateAsync(connectionId, $"Import from file {Path.GetFileName(csvFilePath)} completed successfully. Total inserted: {totalInserted} trades.");

            // Отмечаем в журнале покрытия: этот (символ, день) у нас есть. По журналу
            // критерий закрытого месяца понимает полноту (миграция 015).
            await _tradeRepo.RecordArchiveImportedAsync(symbol, date);

            // (Опционально) Очистка после успешного импорта
            var parentDirectory = Directory.GetParent(csvFilePath)?.FullName;
            if (parentDirectory != null && Directory.Exists(parentDirectory))
            {
                Directory.Delete(parentDirectory, true);
                _logger.LogInformation("Directory {Directory} with processed CSV deleted.", parentDirectory);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Directory {parentDirectory} with processed CSV deleted.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.ShutdownToken.IsCancellationRequested)
        {
            // Штатная остановка Worker'а: импорт прерван shutdown-токеном, а не сбой файла.
            // Логируем как контролируемое завершение; вставка идемпотентна, Hangfire вернёт
            // джобу в очередь и она доработает при следующем старте.
            _logger.LogInformation(
                "Import from file {FileName} cancelled on worker shutdown; will resume on restart.",
                Path.GetFileName(csvFilePath));
            throw; // Перевыбрасываем, чтобы Hangfire не счёл джобу успешной
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing from file {FileName}", Path.GetFileName(csvFilePath));
            await _notifier.SendStatusUpdateAsync(connectionId, $"Error importing from file {Path.GetFileName(csvFilePath)}");
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
