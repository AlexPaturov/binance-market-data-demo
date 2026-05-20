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
        _logger.LogInformation($"{LoggerNames.GetCurrentMethodName()} Central data collector started.");

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
                    _logger.LogWarning("No active symbols to track. Retrying in 5 minutes.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue; // Переходим к следующей итерации цикла
                }

                _logger.LogInformation("Subscribing to {Count} trade streams...", symbolsToTrack.Count);

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
                _logger.LogWarning("Subscription stream ended. Reconnecting in 10 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Штатный выход из цикла
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in subscription loop. Retrying in 30 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // Дожидаемся корректного завершения задачи-писателя
        await writerTask;
        _logger.LogInformation("Central data collector stopped.");
    }

    /// <summary>
    /// Отдельный метод-цикл, который работает как "потребитель" очереди.
    /// Он периодически просыпается, забирает все накопившиеся данные и сохраняет их одной пачкой.
    /// </summary>
    private async Task DatabaseWriterLoop(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database writer task (DatabaseWriterLoop) started.");
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
                        _logger.LogInformation("Saving {Count} trades to the database...", buffer.Count);
                        await _tradeRepository.BulkInsertAsync(buffer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving batch of {Count} trades. Data may be lost.", buffer.Count);
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
            _logger.LogInformation("Database writer task received stop signal.");
            // Попробуем сохранить последние данные из буфера перед выходом
            if (buffer.Any())
            {
                _logger.LogWarning("Saving last {Count} trades before shutdown...", buffer.Count);
                await SaveBufferAsync(buffer);
            }
        }
        finally
        {
            _logger.LogInformation("Database writer task (DatabaseWriterLoop) stopped.");
        }
    }

    // Вспомогательный метод для сохранения, чтобы избежать дублирования кода
    private async Task SaveBufferAsync(List<Trade> buffer)
    {
        if (!buffer.Any()) return;
        try
        {
            await _tradeRepository.BulkInsertAsync(buffer);
            _logger.LogInformation("Last batch of {Count} trades saved successfully.", buffer.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving last batch of {Count} trades.", buffer.Count);
        }
        finally
        {
            buffer.Clear();
        }
    }
}