
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;

namespace BinanceDataCollector.Worker.Workers;

public class BinanceTradesWorker : BackgroundService
{
    private readonly ILogger<BinanceTradesWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public BinanceTradesWorker(ILogger<BinanceTradesWorker> logger, 
        IServiceProvider serviceProvider, 
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Диспетчер сбора данных запущен.");

        // Словарь для хранения запущенных задач и их токенов отмены.
        // Ключ - название символа, Значение - задача и источник ее отмены.
        var runningTasks = new Dictionary<string, (Task, CancellationTokenSource)>();

        // Главный цикл работы воркера. Будет повторяться, пока сервис не будет остановлен.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Обновляем список отслеживаемых символов из базы данных...");

                // --- Шаг 1: Получаем актуальный список активных пар из БД ---
                List<string> activeSymbolsFromDb;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                    activeSymbolsFromDb = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                }
                _logger.LogInformation("В базе данных {Count} активных пар.", activeSymbolsFromDb.Count);

                // --- Шаг 2: Определяем, какие задачи нужно остановить ---
                var symbolsToStop = runningTasks.Keys.Except(activeSymbolsFromDb).ToList();
                foreach (var symbol in symbolsToStop)
                {
                    _logger.LogWarning("Символ {Symbol} больше не активен. Останавливаем сбор данных...", symbol);
                    var (task, cts) = runningTasks[symbol];

                    cts.Cancel(); // Посылаем сигнал отмены задаче
                    await task;   // Ожидаем корректного завершения задачи

                    runningTasks.Remove(symbol); // Удаляем из списка запущенных
                    _logger.LogInformation("Сбор данных по {Symbol} остановлен.", symbol);
                }

                // --- Шаг 3: Определяем, какие задачи нужно запустить ---
                var symbolsToStart = activeSymbolsFromDb.Except(runningTasks.Keys).ToList();
                foreach (var symbol in symbolsToStart)
                {
                    _logger.LogInformation("Найден новый активный символ: {Symbol}. Запускаем сбор данных...", symbol);

                    // Создаем новый источник отмены, связанный с главным токеном
                    var newCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                    // Создаем новый scope для каждой задачи, чтобы у них были свои зависимости
                    var taskScope = _serviceProvider.CreateScope();
                    var dataSyncService = taskScope.ServiceProvider.GetRequiredService<IDataSyncService>();

                    var newTask = dataSyncService.StartTradeCollectionAsync(symbol, newCts.Token);

                    runningTasks.Add(symbol, (newTask, newCts));
                }

                // --- Шаг 4: Ждем перед следующей проверкой ---
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


            // --- Шаг 5: Корректное завершение работы ---
            _logger.LogInformation("Сервис останавливается. Завершаем все активные задачи...");
            foreach (var (task, cts) in runningTasks.Values)
            {
                cts.Cancel();
            }
            await Task.WhenAll(runningTasks.Values.Select(v => v.Item1));
            _logger.LogInformation("Все задачи сбора данных остановлены. Сервис завершил работу.");
        }
    }
}
