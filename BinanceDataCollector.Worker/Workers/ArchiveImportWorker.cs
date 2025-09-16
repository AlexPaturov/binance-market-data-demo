using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;


public class ArchiveImportWorker
{
    private readonly IArchiveService _archiveService;
    private readonly ITradeRepository _tradeRepo;
    private readonly GapProcessingTracker _tracker;
    private readonly ILogger<ArchiveImportWorker> _logger;
    private const int BatchSize = 5000; // <-- КОНФИГУРИРУЕМЫЙ РАЗМЕР ПАЧКИ
    private readonly List<Trade> _batch = new(BatchSize);
    private int _totalInserted;

    public ArchiveImportWorker(
        IArchiveService archiveService,
        ITradeRepository tradeRepo,
        GapProcessingTracker tracker,
        ILogger<ArchiveImportWorker> logger
    )
    {
        _archiveService = archiveService;
        _tradeRepo = tradeRepo;
        _tracker = tracker;
        _logger = logger;
    }

    [Queue("archive_import")] // Самый низкий приоритет
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)] // Одна загрузка - один час максимум
    public async Task ImportArchiveAsync(string symbol, DateOnly date, IJobCancellationToken cancellationToken)
    {
        var token = cancellationToken.ShutdownToken;

        try
        {
            await foreach (var trade in _archiveService.DownloadAndParseTradesAsync(symbol, date, token))
            {
                _batch.Add(trade);

                if (_batch.Count >= BatchSize)
                {
                    await FlushBatch(symbol);
                }
            }

            if (_batch.Any())
                await FlushBatch(symbol);

            _logger.LogInformation("[{Symbol}] Вставлено {TotalCount} сделок за {Date}.", symbol, _totalInserted, date);

        }
        finally
        {
            // ===== ВАЖНО: Снимаем блокировку в любом случае! =====
            _tracker.MarkArchiveAsCompleted(symbol, date);
            _logger.LogInformation("[{Symbol}] Обработка архива за {Date} завершена, блокировка снята.", symbol, date);
            // =======================================================
        }
    }

    private async Task FlushBatch(string symbol)
    {
        await _tradeRepo.BulkInsertAsync(_batch);
        _totalInserted += _batch.Count;
        _logger.LogDebug("[{Symbol}] Вставлена пачка из {Count} сделок...", symbol, _batch.Count);
        _batch.Clear();

        // Каждые 20 батчей агрессивная очистка
        //if (_totalInserted % (BatchSize * 200) == 0)
        //{
        //    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        //    GC.Collect(); // Gen 0
        //    GC.WaitForPendingFinalizers();
        //    GC.Collect(); // Gen 1  
        //    GC.WaitForPendingFinalizers();
        //    GC.Collect(2, GCCollectionMode.Forced, true, true); // Gen 2 + компактизация

        //    _logger.LogInformation("[{Symbol}] ----------------------Принудительная очистка памяти выполнена-----------------------", symbol);
        //}
    }


}
