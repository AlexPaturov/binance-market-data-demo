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

        // Словарь для хранения запущенных задач и их токенов отмены
        var runningTasks = new Dictionary<string, (Task, CancellationTokenSource)>();

        // Регистрируем колбэк на глобальную остановку, чтобы корректно все завершить
        stoppingToken.Register(() =>
        {
            _logger.LogWarning("Получен глобальный сигнал остановки. Завершаем все задачи...");
            foreach (var (_, cts) in runningTasks.Values)
            {
                cts.Cancel();
            }

            _appLifetime.StopApplication();  // Инициируем остановку приложения
        });

        // Главный цикл работы воркера. Будет повторяться, пока сервис не будет остановлен.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Проверяем список отслеживаемых символов из базы данных...");

                List<string> activeSymbolsFromDb;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                    activeSymbolsFromDb = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                }

                if (!activeSymbolsFromDb.Any())
                {
                    _logger.LogWarning("В базе данных нет активных символов. Повторная проверка через 5 минут.");
                }
                else
                {
                    _logger.LogInformation("Требуется отслеживать {Count} активных пар.", activeSymbolsFromDb.Count);
                }

                // --- Останавливаем ненужные задачи ---
                var symbolsToStop = runningTasks.Keys.Except(activeSymbolsFromDb).ToList();
                foreach (var symbol in symbolsToStop)
                {
                    _logger.LogWarning("Символ {Symbol} больше не активен. Останавливаем сбор данных...", symbol);
                    if (runningTasks.TryGetValue(symbol, out var value))
                    {
                        value.Item2.Cancel(); // Посылаем сигнал отмены
                        runningTasks.Remove(symbol);
                    }
                }

                // --- Запускаем новые задачи ---
                var symbolsToStart = activeSymbolsFromDb.Except(runningTasks.Keys).ToList();
                foreach (var symbol in symbolsToStart)
                {
                    _logger.LogInformation("Найден новый активный символ: {Symbol}. Запускаем сбор данных...", symbol);

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var taskScope = _serviceProvider.CreateScope();
                    var dataSyncService = taskScope.ServiceProvider.GetRequiredService<IDataSyncService>();

                    var newTask = dataSyncService.StartTradeCollectionAsync(symbol, cts.Token);
                    runningTasks.Add(symbol, (newTask, cts));
                }

                // --- Ожидаем перед следующей проверкой ---
                _logger.LogInformation("Проверка завершена. Активно отслеживается {Count} пар. Следующая проверка через 1 час.", runningTasks.Count);
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Это ожидаемое исключение при остановке сервиса. Просто выходим из цикла.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Произошла критическая ошибка в главном цикле диспетчера. Повторная попытка через 1 минуту.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("Диспетчер сбора данных полностью остановлен.");
    }
}
