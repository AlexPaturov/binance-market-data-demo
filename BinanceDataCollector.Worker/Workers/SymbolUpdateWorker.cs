using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.MarketScreenService;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически сканирует рынок,
/// находит самые активные пары и обновляет их список в базе данных.
/// </summary>
public class SymbolUpdateWorker
{
    private readonly ILogger<SymbolUpdateWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private int _topN = 40;                         // TODO забирать из конфигурации
    private decimal _minQuoteVolumeInMillion = 10m; // TODO забирать из конфигурации

    public SymbolUpdateWorker(
        ILogger<SymbolUpdateWorker> logger, 
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    [Queue("realtime")]
    public async Task ScanMarketAndUpdateSymbolsAsync()
    {
        _logger.LogInformation("--- Начинаем плановое сканирование рынка ---");

        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                var screener = scope.ServiceProvider.GetRequiredService<MarketScreener>();
                var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var topPairs = await screener.FindTopPairsAsync(topN: _topN, minQuoteVolumeInMillion: _minQuoteVolumeInMillion); // 1. Получаем свежий ТОП пар с Binance

                if (topPairs.Any())
                {
                    var symbolsToTrack = topPairs.Select(p => p.Symbol);
                    _logger.LogInformation("Найдено {Count} активных пар. Обновляем базу данных...", symbolsToTrack.Count());
                    await symbolRepo.UpdateSymbolListAsync(symbolsToTrack);  // Сохраняем полученный список

                    _logger.LogInformation("База данных отслеживаемых символов успешно обновлена.");
                }
                else
                {
                    _logger.LogWarning("Сканер не вернул ни одной пары. Обновление БД пропущено.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Произошла критическая ошибка во время сканирования рынка.");
                throw;
            }
        }

        _logger.LogInformation("--- Плановое сканирование рынка успешно завершено ---");
    }
}
