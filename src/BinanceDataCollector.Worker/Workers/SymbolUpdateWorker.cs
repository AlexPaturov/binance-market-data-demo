using BinanceDataCollector.Application.Analytics.MarketScreeners.Services;
using BinanceDataCollector.Application.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически сканирует рынок,
/// находит самые активные пары и обновляет их список в базе данных.
/// </summary>
public class SymbolUpdateWorker
{
    private readonly ILogger<SymbolUpdateWorker> _logger;
    private readonly MarketScreener _marketScreener;
    private readonly ITrackedSymbolRepository  _trackedSymbolRepository;
    private int _topN = 40;                         // TODO забирать из конфигурации
    private decimal _minQuoteVolumeInMillion = 10m; // TODO забирать из конфигурации

    public SymbolUpdateWorker(
        ILogger<SymbolUpdateWorker> logger, 
        MarketScreener  marketScreener,
        ITrackedSymbolRepository  trackedSymbolRepository
    )
    {
        _logger = logger;
        _marketScreener  = marketScreener;
        _trackedSymbolRepository = trackedSymbolRepository;
    }

    [Queue("realtime")]
    public async Task ScanMarketAndUpdateSymbolsAsync()
    {
        _logger.LogInformation("--- Starting scheduled market scan ---");

        try
        {
            var topPairs =
                await _marketScreener.FindTopPairsAsync(topN: _topN,
                    minQuoteVolumeInMillion: _minQuoteVolumeInMillion); // 1. Получаем свежий ТОП пар с Binance

            if (topPairs.Any())
            {
                var symbolsToTrack = topPairs.Select(p => p.Symbol);
                _logger.LogInformation("Found {Count} active pairs. Updating database...",
                    symbolsToTrack.Count());
                await _trackedSymbolRepository.UpdateSymbolListAsync(symbolsToTrack); // Сохраняем полученный список

                _logger.LogInformation("Tracked symbols database updated successfully.");
            }
            else
            {
                _logger.LogWarning("Market screener returned no pairs. Database update skipped.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error during market scan.");
            throw;
        }

        _logger.LogInformation("--- Scheduled market scan completed successfully ---");
    }
}
