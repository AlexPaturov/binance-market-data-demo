using BinanceDataCollector.Application.Interfaces;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически агрегирует сырые сделки (тиковые данные)
/// из таблицы Trades в минутные свечи (OHLCV) в таблице Ohlcv_1min.
/// </summary>
public class OhlcvAggregatorWorker : BackgroundService
{
    private readonly ILogger<OhlcvAggregatorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public OhlcvAggregatorWorker(ILogger<OhlcvAggregatorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер-агрегатор свечей запущен.");
        // Ждем немного перед первым запуском
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogTrace("Запускаем плановую агрегацию свечей...");
                using (var scope = _serviceProvider.CreateScope())
                {
                    // Нам нужен любой репозиторий, чтобы получить доступ к Dapper и БД
                    var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

                    // Вызываем нашу мощную хранимую процедуру
                    await tradeRepo.ExecuteAggregationAsync();
                }
                _logger.LogTrace("Агрегация свечей завершена.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Произошла ошибка во время агрегации свечей.");
            }

            // Ждем 1 минуту перед следующим запуском
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
