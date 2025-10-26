using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using System.Collections.Concurrent;


namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Центральный воркер, который управляет подпиской на WebSocket
/// и периодической записью собранных данных в базу.
/// </summary>
public class BinanceCollectorWorker : BackgroundService
{
    private readonly ILogger<BinanceCollectorWorker> _logger;
    private readonly ITrackedSymbolRepository  _trackedSymbolRepository;
    private readonly IBinanceService  _binanceService;
    private readonly ITradeRepository  _tradeRepository;
    private readonly double _collectorStartDelayMinutes = 5; // задержк запуска проверки
    private readonly double _mainWhileRestartDelaySeconds = 10; // задержка перезапуска главного цикла

    // Потокобезопасная очередь для сбора сделок.
    // Выступает в роли буфера между быстрым получением данных и более медленной записью в БД.
    private readonly ConcurrentQueue<Trade> _tradeQueue = new();

    public BinanceCollectorWorker(
        ILogger<BinanceCollectorWorker> logger,
        ITrackedSymbolRepository  trackedSymbolRepository, 
        IBinanceService  binanceService, 
        ITradeRepository tradeRepository)
    {
        _logger = logger;
        _trackedSymbolRepository = trackedSymbolRepository;
        _binanceService = binanceService;
        _tradeRepository = tradeRepository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"{LoggerNames.GetCurrentMethodName()} Центральный сборщик данных запущен.");

        // --- Запускаем задачу-писателя (Consumer) в фоновом режиме ---
        var writerTask = Task.Run(() => DatabaseWriterLoop(stoppingToken), stoppingToken);

        // --- Основной цикл подписки (Producer) ---
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var symbolsToTrack = (await _trackedSymbolRepository.GetActiveSymbolsAsync()).ToList();
                if (!symbolsToTrack.Any())
                {
                    _logger.LogWarning("Нет активных символов для отслеживания. Повторная проверка через 5 минут.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue; // Переходим к следующей итерации цикла
                }

                _logger.LogInformation("Начинаем подписку на {Count} потоков сделок...", symbolsToTrack.Count);

                // Подписываемся на ВСЕ потоки ОДНИМ вызовом
                await _binanceService.SubscribeToMultipleTradesAsync(symbolsToTrack,
                    (trade) => // Единый обработчик для всех сделок
                    {
                        // Просто кладем сделку в потокобезопасную очередь
                        _tradeQueue.Enqueue(trade);
                    },
                    stoppingToken);

                // Если подписка по какой-то причине завершилась (например, потеря связи),
                // мы просто залогируем это и цикл while автоматически попробует переподписаться.
                _logger.LogWarning("Поток подписки завершился. Попытка переподключения через 10 секунд...");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Штатный выход из цикла
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в основном цикле подписки. Повторная попытка через 30 секунд.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // Дожидаемся корректного завершения задачи-писателя
        await writerTask;
        _logger.LogInformation("Центральный сборщик данных остановлен.");
    }

    /// <summary>
    /// Отдельный метод-цикл, который работает как "потребитель" очереди.
    /// Он периодически просыпается, забирает все накопившиеся данные и сохраняет их одной пачкой.
    /// </summary>
    private async Task DatabaseWriterLoop(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Задача-писатель в БД (DatabaseWriterLoop) запущена.");
        var buffer = new List<Trade>();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2)); // Периодичность записи

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Выгребаем все из очереди в локальный буфер
                while (_tradeQueue.TryDequeue(out var trade))
                {
                    buffer.Add(trade);
                }

                if (buffer.Count > 0)
                {
                    try
                    {
                        _logger.LogInformation("Сохраняем {Count} сделок в базу данных...", buffer.Count);
                        await _tradeRepository.BulkInsertAsync(buffer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при сохранении пачки из {Count} сделок. Данные могут быть утеряны.", buffer.Count);
                        // Очищаем буфер в любом случае, чтобы не пытаться записать "битые" данные снова
                    }
                    finally
                    {
                        buffer.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Задача-писатель в БД получила сигнал остановки.");
            // Попробуем сохранить последние данные из буфера перед выходом
            if (buffer.Any())
            {
                _logger.LogWarning("Сохраняем последние {Count} сделок перед завершением...", buffer.Count);
                await SaveBufferAsync(buffer);
            }
        }
        finally
        {
            _logger.LogInformation("Задача-писатель в БД (DatabaseWriterLoop) остановлена.");
        }
    }

    // Вспомогательный метод для сохранения, чтобы избежать дублирования кода
    private async Task SaveBufferAsync(List<Trade> buffer)
    {
        if (!buffer.Any()) return;
        try
        {
            await _tradeRepository.BulkInsertAsync(buffer);
            _logger.LogInformation("Последняя пачка из {Count} сделок успешно сохранена.", buffer.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сохранении последней пачки из {Count} сделок.", buffer.Count);
        }
        finally
        {
            buffer.Clear();
        }
    }
}