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

    // Статический объект для блокировки. Гарантирует, что только один
    // экземпляр этого воркера (даже в теории) сможет выполнять работу.
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public OhlcvAggregatorWorker(ILogger<OhlcvAggregatorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер-агрегатор свечей запущен.");
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Пытаемся "захватить" семафор. Если он уже захвачен,
            // просто пропускаем этот цикл и ждем следующей минуты.
            if (!await _semaphore.WaitAsync(0, stoppingToken))
            {
                _logger.LogWarning("Предыдущая задача агрегации все еще выполняется. Пропускаем текущий запуск.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            try
            {
                _logger.LogTrace("Запускаем плановую агрегацию свечей...");
                using (var scope = _serviceProvider.CreateScope())
                {
                    var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
                    await tradeRepo.ExecuteAggregationAsync();
                }
                _logger.LogTrace("Агрегация свечей завершена.");
            }
            catch (Exception ex)
            {
                // Логируем ошибку, но не "роняем" весь воркер
                _logger.LogError(ex, "Произошла ошибка во время агрегации свечей.");
            }
            finally
            {
                // ОБЯЗАТЕЛЬНО освобождаем семафор, чтобы следующий цикл мог работать.
                _semaphore.Release();
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
