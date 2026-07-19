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
    private readonly IServiceScopeFactory _scopeFactory;

    // Потокобезопасная очередь для сбора сделок.
    // Выступает в роли буфера между быстрым получением данных и более медленной записью в БД.
    private readonly ConcurrentQueue<Trade> _tradeQueue = new();

    /// <summary>
    /// Воркер — синглтон (<see cref="BackgroundService"/>/HostedService), а репозитории и клиент
    /// Binance зарегистрированы как scoped. Синглтон не может держать scoped-зависимость в
    /// конструкторе: она пережила бы свой скоуп (captive dependency). Поэтому в конструктор берём
    /// только <see cref="IServiceScopeFactory"/>, а сами сервисы резолвим из скоупа под каждую
    /// единицу работы — как и остальные воркеры слоя (напр. <c>OrderBookCollectorWorker</c>).
    ///
    /// Прямая инъекция scoped-сервисов в этот класс валилась бы на старте в Development
    /// (там DI включает ValidateScopes/ValidateOnBuild); на проде (Production) валидация
    /// выключена, и та же ошибка прошла бы молча.
    /// </summary>
    public BinanceCollectorWorker(
        ILogger<BinanceCollectorWorker> logger,
        IServiceScopeFactory scopeFactory
        )
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
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
                // Скоуп на одну сессию подписки: сервисы живут ровно столько, сколько держится
                // соединение, и освобождаются при переподписке на следующей итерации.
                using var scope = _scopeFactory.CreateScope();
                var symbolRepository = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();

                var symbolsToTrack = (await symbolRepository.GetActiveSymbolsAsync()).ToList();
                if (!symbolsToTrack.Any())
                {
                    _logger.LogWarning("No active symbols to track. Retrying in 5 minutes.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue; // Переходим к следующей итерации цикла
                }

                _logger.LogInformation("Subscribing to {Count} trade streams...", symbolsToTrack.Count);

                // Подписываемся на ВСЕ потоки ОДНИМ вызовом
                await binanceService.SubscribeToMultipleTradesAsync(symbolsToTrack,
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
                    // Запись идёт через SaveBufferAsync: скоуп под ITradeRepository открывается там.
                    await SaveBufferAsync(buffer);
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

    /// <summary>
    /// Пишет накопленную пачку сделок в БД. Скоуп создаётся здесь, под каждую запись:
    /// метод зовётся из долгоживущего писательского цикла (этот класс — синглтон), а
    /// <see cref="ITradeRepository"/> — scoped; держать его дольше скоупа нельзя (см. конструктор).
    /// Репозиторий соединение с БД открывает и закрывает внутри вызова, так что скоуп на пачку —
    /// это цена не за само соединение, а за корректное время жизни зависимости.
    /// </summary>
    private async Task SaveBufferAsync(List<Trade> buffer)
    {
        if (buffer.Count == 0) return;
        try
        {
            _logger.LogInformation("Saving {Count} trades to the database...", buffer.Count);

            using var scope = _scopeFactory.CreateScope();
            var tradeRepository = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            await tradeRepository.BulkInsertAsync(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving batch of {Count} trades. Data may be lost.", buffer.Count);
            // Очищаем буфер в любом случае, чтобы не пытаться записать "битые" данные снова.
        }
        finally
        {
            buffer.Clear();
        }
    }
}