using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers.Archives;

public class OnlineArchiveImportWorker
{
    private readonly IArchiveService _archiveService;
    private readonly ITradeRepository _tradeRepo;
    private readonly GapProcessingTracker _tracker;
    private readonly ILogger<OnlineArchiveImportWorker> _logger;
    private const int BatchSize = 5000; // <-- КОНФИГУРИРУЕМЫЙ РАЗМЕР ПАЧКИ
    private readonly List<Trade> _batch = new(BatchSize);
    private int _totalInserted;

    public OnlineArchiveImportWorker(
        IArchiveService archiveService,
        ITradeRepository tradeRepo,
        GapProcessingTracker tracker,
        ILogger<OnlineArchiveImportWorker> logger)
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

            _logger.LogInformation("[{Symbol}] Inserted {TotalCount} trades for {Date}.", symbol, _totalInserted, date);
        }
        finally
        {
            _tracker.MarkArchiveAsCompleted(symbol, date); // Снимаем блокировку
            _logger.LogInformation("[{Symbol}] Archive processing for {Date} completed, lock released.", symbol, date);
        }
    }

    private async Task FlushBatch(string symbol)
    {
        await _tradeRepo.BulkInsertAsync(_batch);
        _totalInserted += _batch.Count;
        _logger.LogDebug("[{Symbol}] Inserted batch of {Count} trades...", symbol, _batch.Count);
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
