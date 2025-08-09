using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Application.Services;

// Этот сервис использует интерфейсы для выполнения своей задачи
public class DataSyncService
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


    public async Task StartTradeCollectionAsync(string symbol)
    {
        _logger.LogInformation("Запускаем сбор данных по сделкам для {Symbol}", symbol);

        var tradesBuffer = new List<Trade>();
        var lastFlushTime = DateTime.UtcNow;

        await _binanceService.SubscribeToTradesAsync(symbol, async (trade) => {
            List<Trade>? tradesToInsert = null;

            // 2. Начинаем блокировку. Только один поток может войти сюда одновременно.
            lock (_bufferLock)
            {
                tradesBuffer.Add(trade);
                _logger.LogTrace("Получена сделка {Id}, размер буфера: {Count}", trade.TradeId, tradesBuffer.Count);

                if (tradesBuffer.Count >= 1000 || DateTime.UtcNow - lastFlushTime > TimeSpan.FromSeconds(5))
                {
                    if (tradesBuffer.Count > 0)
                    {
                        // Мы внутри блокировки, поэтому эти три операции атомарны
                        tradesToInsert = tradesBuffer.ToList(); // Копируем
                        tradesBuffer.Clear();                   // Очищаем
                        lastFlushTime = DateTime.UtcNow;        // Сбрасываем таймер
                    }
                }
            } // 3. Блокировка здесь снимается. Другие потоки могут добавлять сделки в буфер.

            // 4. Если мы создали копию для вставки, выполняем саму вставку.
            //    Это происходит ВНЕ блокировки, что критически важно для производительности.
            if (tradesToInsert != null)
            {
                _logger.LogInformation("Сохраняем {Count} сделок в базу данных...", tradesToInsert.Count);
                try
                {
                    await _tradeRepository.BulkInsertAsync(tradesToInsert);
                }
                catch (Exception ex)
                {
                    // Логируем ошибку, но не останавливаем весь процесс.
                    _logger.LogError(ex, "Ошибка при выполнении BulkInsertAsync.");
                }
            }
        });
    }

    // Обработчик полученных сделок
    //private async Task OnTradeReceived(Trade trade)
    //{
    //    // Здесь можно добавить логику обработки сделки
    //    // Например, сохранить сделку в базе данных
    //    await _tradeRepository.BulkInsertAsync(new[] { trade });
    //}
}
