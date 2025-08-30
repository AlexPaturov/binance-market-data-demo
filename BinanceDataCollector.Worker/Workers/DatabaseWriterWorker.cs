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
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2)); // Таймер на 2 секунды

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Вычитываем все, что есть в очереди на данный момент
            while (_tradeQueueReader.TryRead(out var trade))
            {
                buffer.Add(trade);
            }

            if (buffer.Count > 0)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

                    _logger.LogInformation("Сохраняем {Count} сделок в базу данных...", buffer.Count);
                    await tradeRepo.BulkInsertAsync(buffer);
                    buffer.Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при сохранении пачки сделок. {Count} сделок могут быть утеряны.", buffer.Count);
                    // В реальной системе здесь нужна логика сохранения "битой" пачки в файл
                    buffer.Clear(); // Очищаем буфер, чтобы не пытаться записать те же данные снова
                }
            }
        }
    }
}