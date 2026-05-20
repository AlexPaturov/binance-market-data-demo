using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace BinanceDataCollector.Worker.Workers.Archives;

public class OnlineArchiveImportWorker
{
    private readonly IArchiveService _archiveService;
    private readonly ITradeRepository _tradeRepo;
    private readonly GapProcessingTracker _tracker;
    private readonly ILogger<OnlineArchiveImportWorker> _logger;
    private readonly IOptions<ArchivesSettings> _options;
    private const int BatchSize = 5000; // <-- КОНФИГУРИРУЕМЫЙ РАЗМЕР ПАЧКИ
    private readonly List<Trade> _batch = new(BatchSize);
    private int _totalInserted;

    public OnlineArchiveImportWorker(
        IArchiveService archiveService,
        ITradeRepository tradeRepo,
        IOptions<ArchivesSettings> options,
        GapProcessingTracker tracker,
        ILogger<OnlineArchiveImportWorker> logger)
    {
        _archiveService = archiveService;
        _tradeRepo = tradeRepo;
        _options = options;
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

    // НОВЫЙ МЕТОД, для вызова из UI
    [Queue("archive_import")]
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
    public async Task ImportFromLocalFileAsync(string fileName, IJobCancellationToken cancellationToken)
    {
        // var (symbol, date) = ParseSymbolAndDateFromFileName(fileName); // old version
        var (symbol, date) = ArchiveFileNameParser.Parse(fileName);
        var filePath = Path.Combine(_options.Value.TradeArcihvesPath, fileName);

        try
        {
            await foreach (var trade in _archiveService.ParseTradesFromLocalZipAsync(filePath, symbol, cancellationToken.ShutdownToken))
            {
                _batch.Add(trade);

                if (_batch.Count >= BatchSize)
                {
                    await FlushBatch(symbol);
                }

                if (_batch.Any())
                    await FlushBatch(symbol);

                _logger.LogInformation("[{Symbol}] Inserted {TotalCount} trades for {Date}.", symbol, _totalInserted, date);
            }
        }
        finally
        {
            _tracker.MarkArchiveAsCompleted(symbol, date); // Снимаем блокировку
            _logger.LogInformation("[{Symbol}] Archive processing for {Date} completed, lock released.", symbol, date);
        }
    }

    private (string, DateOnly) ParseSymbolAndDateFromFileName(string fileName) 
    {
        // Паттерн: 
        // ^(?<symbol>.+?) - от начала строки захватываем любые символы (не жадно) в группу "symbol"
        // -trades-        - дословно ищем "-trades-"
        // (?<date>\d{4}-\d{2}-\d{2}) - захватываем дату в формате YYYY-MM-DD в группу "date"
        // .zip$           - файл должен заканчиваться на .zip
        var regex = new Regex(@"^(?<symbol>.+?)-trades-(?<date>\d{4}-\d{2}-\d{2})\.zip$");

        var match = regex.Match(fileName);

        if (match.Success)
        {
            var symbol = match.Groups["symbol"].Value;
            var dateString = match.Groups["date"].Value;

            if (DateOnly.TryParse(dateString, out var date))
            {
                return (symbol, date);
            }
        }

        _logger.LogWarning("Failed to parse file name using Regex: {FileName}", fileName);
        return ("UNKNOWN", DateOnly.MinValue);
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
