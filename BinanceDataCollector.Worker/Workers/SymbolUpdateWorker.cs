using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.MarketScreenService;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически сканирует рынок,
/// находит самые активные пары и обновляет их список в базе данных.
/// </summary>
public class SymbolUpdateWorker : BackgroundService
{
    private readonly ILogger<SymbolUpdateWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public SymbolUpdateWorker(ILogger<SymbolUpdateWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер обновления списка символов запущен.");

        // Ждем 1 минуту перед первым запуском, чтобы дать основному сервису стартовать
        //await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Начинаем плановое сканирование рынка...");

                // Создаем scope для получения наших сервисов
                using (var scope = _serviceProvider.CreateScope())
                {
                    var screener = scope.ServiceProvider.GetRequiredService<MarketScreener>();
                    var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();

                    // 1. Получаем свежий ТОП пар с Binance
                    var topPairs = await screener.FindTopPairsAsync(topN: 40, minQuoteVolumeInMillion: 10m);

                    if (topPairs.Any())
                    {
                        var symbolsToTrack = topPairs.Select(p => p.Symbol);
                        _logger.LogInformation("Найдено {Count} активных пар. Обновляем базу данных...", symbolsToTrack.Count());

                        // 2. Вызываем метод репозитория для сохранения списка
                        await symbolRepo.UpdateSymbolListAsync(symbolsToTrack);

                        _logger.LogInformation("База данных отслеживаемых символов успешно обновлена.");
                    }
                    else
                    {
                        _logger.LogWarning("Сканер не вернул ни одной пары. Обновление БД пропущено.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Произошла ошибка во время сканирования рынка.");
            }

            _logger.LogInformation("Сканирование завершено. Следующий запуск через 24 часа.");
            // Ожидаем перед следующим запуском
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }

        _logger.LogInformation("Воркер обновления списка символов останавливается.");
    }
}
