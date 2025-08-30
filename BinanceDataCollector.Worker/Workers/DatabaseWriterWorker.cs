using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BinanceDataCollector.Worker.Workers;

public class DatabaseWriterWorker : BackgroundService
{
    private readonly ILogger<DatabaseWriterWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ChannelReader<Trade> _tradeQueueReader; // Используем "читателя" из очереди

    public DatabaseWriterWorker(
        ILogger<DatabaseWriterWorker> logger,
        IServiceProvider serviceProvider,
        Channel<Trade> tradeQueue)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _tradeQueueReader = tradeQueue.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер записи в базу данных запущен.");
        var buffer = new List<Trade>();
        // Максимальный размер пачки для записи
        const int batchSize = 10000;
        // Максимальное время ожидания перед принудительной записью
        var flushTimeout = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Асинхронно ждем ПЕРВОГО элемента в очереди
                var firstTrade = await _tradeQueueReader.ReadAsync(stoppingToken);
                buffer.Add(firstTrade);

                // 2. Быстро собираем "хвост" - все, что УЖЕ есть в очереди,
                // но не больше, чем наш размер пачки.
                while (buffer.Count < batchSize && _tradeQueueReader.TryRead(out var nextTrade))
                {
                    buffer.Add(nextTrade);
                }

                // 3. Если мы собрали полную пачку, сразу ее сохраняем.
                if (buffer.Count >= batchSize)
                {
                    await SaveBufferAsync(buffer, stoppingToken);
                    continue; // Начинаем новый цикл немедленно
                }

                // 4. Если пачка неполная, ждем еще немного, вдруг что-то придет.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(flushTimeout);
                try
                {
                    // Ждем, пока не придет ЕЩЕ ОДИН элемент или не сработает таймаут
                    await _tradeQueueReader.WaitToReadAsync(timeoutCts.Token);

                    // Если дождались, добираем остатки
                    while (buffer.Count < batchSize && _tradeQueueReader.TryRead(out var finalTrade))
                    {
                        buffer.Add(finalTrade);
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Это не ошибка, это сработал наш таймаут. Просто идем сохранять то, что есть.
                }

                // 5. Сохраняем все, что накопилось.
                await SaveBufferAsync(buffer, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Штатный выход
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка в DatabaseWriterWorker.");
                await Task.Delay(5000, stoppingToken); // Пауза в случае серьезного сбоя
            }
        }
    }

    private async Task SaveBufferAsync(List<Trade> buffer, CancellationToken stoppingToken)
    {
        if (!buffer.Any()) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            _logger.LogInformation("Сохраняем {Count} сделок в базу данных...", buffer.Count);
            await tradeRepo.BulkInsertAsync(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сохранении пачки из {Count} сделок.", buffer.Count);
        }
        finally
        {
            buffer.Clear();
        }
    }
}