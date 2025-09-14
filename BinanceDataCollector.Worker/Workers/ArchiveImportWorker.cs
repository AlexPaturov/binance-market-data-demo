using BinanceDataCollector.Application.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;


public class ArchiveImportWorker
{
    private readonly IArchiveService _archiveService;
    private readonly ITradeRepository _tradeRepo;

    public ArchiveImportWorker(
        IArchiveService archiveService,
        ITradeRepository tradeRepo
    )
    {
        _archiveService = archiveService;
        _tradeRepo = tradeRepo;
    }

    [Queue("archive_import")] // Самый низкий приоритет
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)] // Одна загрузка - один час максимум
    public async Task ImportArchiveAsync(string symbol, DateOnly date, CancellationToken cancellationToken)
    {
        // 1. Скачать и распарсить
        var trades = await _archiveService.DownloadAndParseTradesAsync(symbol, date, CancellationToken.None);

        if (trades.Any())
        {
            await _tradeRepo.BulkInsertAsync(trades);
        }
    }
}
