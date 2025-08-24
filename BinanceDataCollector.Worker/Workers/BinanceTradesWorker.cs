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
        var runningTasks = new Dictionary<string, (Task Task, CancellationTokenSource Cts)>();

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
                    if (runningTasks.TryGetValue(symbol, out var taskInfo))
                    {
                        _logger.LogWarning("[{Symbol}] Символ больше не активен. Останавливаем сбор.", symbol);
                        taskInfo.Cts.Cancel();
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
                    var task = Task.Run(async () =>
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
                                try 
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                                }
                                catch (OperationCanceledException operationCanceledException) 
                                {
                                    /* Отмена во время задержки */
                                    _logger.LogError(operationCanceledException, "[{Symbol}] Задача сбора данных упала. Отмена во время задержки .", symbol);
                                    break; 
                                }
                            }
                        }
                    }, cts.Token);

                    runningTasks.Add(symbol,(task, cts));
                }

                _logger.LogInformation("Проверка завершена. Активно отслеживается {Count} пар. Следующая проверка через 1 час.", runningTasks.Count);

                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Получен сигнал остановки во время ожидания следующей проверки.");
                    break;
                }
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
            // --- ФИНАЛЬНЫЙ БЛОК ЗАВЕРШЕНИЯ ---
            if (runningTasks.Any())
            {
                _logger.LogWarning("Останавливаем {Count} активных задач сбора данных...", runningTasks.Count);

                try
                {
                    // 1. Асинхронно посылаем сигнал отмены всем дочерним задачам
                    var cancelTasks = runningTasks.Values.Select(taskInfo => taskInfo.Cts.CancelAsync());
                    await Task.WhenAll(cancelTasks);

                    _logger.LogDebug("Сигналы отмены отправлены всем задачам");

                    // 2. Собираем все задачи в один список
                    var allTasks = runningTasks.Values.Select(v => v.Task).ToList();

                    // 3. Асинхронно ждем, пока ВСЕ задачи не завершатся (с таймаутом)
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await Task.WhenAll(allTasks).WaitAsync(timeoutCts.Token);

                    _logger.LogInformation("Все задачи сбора данных успешно остановлены.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Таймаут при ожидании завершения задач (30 секунд). Принудительное завершение.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при завершении задач сбора данных.");
                }
                finally
                {
                    // 4. Освобождаем ресурсы CancellationTokenSource
                    foreach (var (_, cts) in runningTasks.Values)
                    {
                        try
                        {
                            cts.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Ошибка при освобождении CancellationTokenSource");
                        }
                    }

                    runningTasks.Clear();
                }
            }

            _logger.LogInformation("Диспетчер сбора данных полностью остановлен.");
        }
    }
}
