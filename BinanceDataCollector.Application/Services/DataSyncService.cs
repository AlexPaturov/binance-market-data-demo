using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Application.Services;

// Этот сервис использует интерфейсы для выполнения своей задачи
public class DataSyncService : IDataSyncService
{
    private readonly ILogger<DataSyncService> _logger;
    private readonly ITradeRepository _tradeRepository;
    private readonly IBinanceService _binanceService;
    private readonly object _bufferLock = new object(); // 1. Создаем объект-заглушку для блокировки. Он должен быть приватным.

    public DataSyncService(ILogger<DataSyncService> logger,
        ITradeRepository tradeRepository,
        IBinanceService binanceService)
    {
        _logger = logger;
        _tradeRepository = tradeRepository;
        _binanceService = binanceService;
    }


    public async Task StartTradeCollectionAsync(string symbol, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Запускаем сбор данных по сделкам для {Symbol}", symbol);

        var tradesBuffer = new List<Trade>();
        var lastFlushTime = DateTime.UtcNow;

        try
        {
            // Эта TaskCompletionSource позволит нам контролировать завершение задачи
            // и дождаться отписки от WebSocket.
            var tcs = new TaskCompletionSource();

            // Регистрируем колбэк на отмену, который завершит нашу задачу
            cancellationToken.Register(() => tcs.TrySetCanceled());

            // Подписываемся на поток сделок
            await _binanceService.SubscribeToTradesAsync(symbol,
                async (trade) => // Обработчик каждой новой сделки
                {
                    List<Trade>? tradesToInsert = null;

                    // Блокируем буфер для потокобезопасного доступа
                    lock (_bufferLock)
                    {
                        tradesBuffer.Add(trade);

                        // Проверяем, пора ли сбрасывать данные в базу
                        if (tradesBuffer.Count >= 1000 || DateTime.UtcNow - lastFlushTime > TimeSpan.FromSeconds(5))
                        {
                            if (tradesBuffer.Any())
                            {
                                tradesToInsert = tradesBuffer.ToList();
                                tradesBuffer.Clear();
                                lastFlushTime = DateTime.UtcNow;
                            }
                        }
                    }

                    // Если мы подготовили пачку данных, сохраняем ее.
                    // Важно делать это ВНЕ блокировки 'lock'.
                    if (tradesToInsert != null)
                    {
                        _logger.LogInformation("[{Symbol}] Сохраняем {Count} сделок...", symbol, tradesToInsert.Count);
                        try
                        {
                            await _tradeRepository.BulkInsertAsync(tradesToInsert);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[{Symbol}] Ошибка при сохранении пачки сделок.", symbol);
                        }
                    }
                },
                cancellationToken);

            _logger.LogInformation("[{Symbol}] Успешно подписан на поток WebSocket.", symbol);

            // Ожидаем, пока не придет сигнал отмены
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            // Это ожидаемое завершение работы. Просто логируем и      выходим.
            _logger.LogWarning("[{Symbol}] Получен сигнал отмены. Модуль сбора данных останавливается.", symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Критическая ошибка в модуле сбора данных.", symbol);
        }
        finally
        {
            _logger.LogInformation("[{Symbol}] Модуль сбора данных полностью остановлен.", symbol);
        }
    }

    // Обработчик полученных сделок
    //private async Task OnTradeReceived(Trade trade)
    //{
    //    // Здесь можно добавить логику обработки сделки
    //    // Например, сохранить сделку в базе данных
    //    await _tradeRepository.BulkInsertAsync(new[] { trade });
    //}
}
