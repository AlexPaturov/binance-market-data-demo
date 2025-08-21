using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;

namespace BinanceDataCollector.Worker.Workers;

public class BinanceTradesWorker : BackgroundService
{
    private readonly ILogger<BinanceTradesWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _appLifetime;

    public BinanceTradesWorker(ILogger<BinanceTradesWorker> logger, 
        IServiceProvider serviceProvider, 
        IConfiguration configuration, 
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Диспетчер сбора данных запущен.");
        var runningTasks = new Dictionary<string, CancellationTokenSource>();

        try
        {
            // Главный цикл, который никогда не завершается сам по себе
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Проверяем список отслеживаемых символов...");

                List<string> activeSymbolsFromDb;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                    activeSymbolsFromDb = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                }

                // --- Останавливаем ненужные ---
                var symbolsToStop = runningTasks.Keys.Except(activeSymbolsFromDb).ToList();
                foreach (var symbol in symbolsToStop)
                {
                    if (runningTasks.TryGetValue(symbol, out var cts))
                    {
                        _logger.LogWarning("[{Symbol}] Символ больше не активен. Останавливаем сбор.", symbol);
                        cts.Cancel();
                        runningTasks.Remove(symbol);
                    }
                }

                // --- Запускаем новые ---
                var symbolsToStart = activeSymbolsFromDb.Except(runningTasks.Keys).ToList();
                foreach (var symbol in symbolsToStart)
                {
                    _logger.LogInformation("[{Symbol}] Запускаем сбор для нового/возобновленного символа.", symbol);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                    // Мы не храним Task, а просто запускаем его в фоне.
                    // cts - это наш "пульт управления" для этой задачи.
                    _ = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                using var scope = _serviceProvider.CreateScope();
                                var dataSyncService = scope.ServiceProvider.GetRequiredService<IDataSyncService>();
                                await dataSyncService.StartTradeCollectionAsync(symbol, cts.Token);
                            }
                            catch (OperationCanceledException) { /* Ожидаемое завершение */ }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[{Symbol}] Задача сбора данных упала. Перезапуск через 30 секунд.", symbol);
                                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                            }
                        }
                    }, cts.Token);

                    runningTasks.Add(symbol, cts);
                }

                _logger.LogInformation("Проверка завершена. Активно отслеживается {Count} пар. Следующая проверка через 1 час.", runningTasks.Count);
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogCritical(ex, "Штатное завершение работы при остановке всего сервиса.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Критическая ошибка в главном цикле диспетчера.");
        }
        finally
        {
            _logger.LogInformation("Диспетчер сбора данных полностью остановлен.");
        }
    }
}
