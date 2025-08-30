using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace BinanceDataCollector.Application.Services;

public class DataSyncService : IDataSyncService
{
    private readonly ILogger<DataSyncService> _logger;
    private readonly IBinanceService _binanceService;
    private readonly ChannelWriter<Trade> _tradeQueueWriter; // Используем "писателя" в очередь

    // Конструктор теперь принимает ChannelWriter<Trade> вместо ITradeRepository
    public DataSyncService(
        ILogger<DataSyncService> logger,
        IBinanceService binanceService,
        Channel<Trade> tradeQueue) // DI-контейнер предоставит нам всю очередь
    {
        _logger = logger;
        _binanceService = binanceService;
        _tradeQueueWriter = tradeQueue.Writer; // Мы берем из нее только "писателя"
    }

    public async Task StartTradeCollectionAsync(string symbol, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Symbol}] Запуск модуля сбора данных...", symbol);

        try
        {
            // Теперь мы просто "запускаем и забываем" (fire and forget) подписку,
            await _binanceService.SubscribeToTradesAsync(symbol,
                async (trade) => // Обработчик каждой новой сделки
                {
                    // Просто пытаемся записать сделку в очередь.
                    // TryWrite - это неблокирующая операция, она работает мгновенно.
                    if (!_tradeQueueWriter.TryWrite(trade))
                    {
                        _logger.LogWarning("[{Symbol}] Не удалось добавить сделку в очередь. Очередь заполнена.", symbol);
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException) // Это штатное завершение, когда CancellationToken отменяется
        {
            _logger.LogInformation("[{Symbol}] Модуль сбора данных остановлен сигналом отмены.", symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Критическая ошибка в модуле сбора данных.", symbol);
        }
    }
}