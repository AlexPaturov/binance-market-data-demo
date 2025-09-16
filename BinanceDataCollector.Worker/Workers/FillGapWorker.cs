using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Выполняет работу по заполнению одной конкретной дыры в TradeId,
/// используя API для получения сырых сделок.
/// </summary>
// Атрибут [Queue] здесь, чтобы задачи, поставленные вручную,
// попадали в правильную очередь.
[Queue("quick_audit")]
public class FillGapWorker
{
    private readonly GapProcessingTracker _tracker;
    private readonly IBinanceService _binanceService;
    private readonly ITradeRepository _tradeRepo;
    private readonly ILogger<FillGapWorker> _logger;
    private const int BatchSize = 1000; // API отдает максимум 1000 за раз

    public FillGapWorker(
        GapProcessingTracker tracker,
        IBinanceService binanceService,
        ITradeRepository tradeRepo,
        ILogger<FillGapWorker> logger
    )
    {
        _tracker = tracker;
        _binanceService = binanceService;
        _tradeRepo = tradeRepo;
        _logger = logger;
    }

    /// <summary>
    /// Основной метод, который будет вызываться Hangfire.
    /// </summary>
    [DisableConcurrentExecution(10 * 60)] // Не запускать параллельно для одной и той же дыры
    public async Task FillWithApiAsync(string symbol, DataGap gap, IJobCancellationToken cancellationToken)
    {
        var token = cancellationToken.ShutdownToken;
        long currentFromId = gap.GapStart; // Начинаем с первого недостающего ID
        long gapEndId = gap.GapEnd;
        long tradesToFetch = gapEndId - currentFromId + 1;

        if (tradesToFetch <= 0) return;

        try
        {
            using (_logger.TimedOperation(LogLevel.Warning, "[{Symbol}] Заполнение дыры в {Count} сделок с ID {StartId} по {EndId}", 
                symbol, tradesToFetch, currentFromId, gapEndId))
            {
                while (currentFromId <= gapEndId && !token.IsCancellationRequested)
                {
                    long remainingTrades = gapEndId - currentFromId + 1;
                    int currentLimit = (int)Math.Min(remainingTrades, BatchSize);

                    var fetchResult = await _binanceService.GetHistoricalRawTradesAsync(symbol, currentFromId, token, currentLimit);

                    switch (fetchResult.Status)
                    {
                        case FetchStatus.Success:
                            if (!fetchResult.Data.Any())
                            {
                                _logger.LogError("[{Symbol}] Binance не вернул данные для ID >= {FromId}, хотя должен был. Заполнение дыры прервано.", symbol, currentFromId);
                                // Выбрасываем исключение, чтобы Hangfire повторил попытку
                                throw new InvalidOperationException($"Binance API returned no data for a presumed gap starting at {currentFromId} for {symbol}.");
                            }

                            await _tradeRepo.BulkInsertAsync(fetchResult.Data);

                            #region Делаем красивый лог
                            var firstTrade = fetchResult.Data.First();
                            var lastTrade = fetchResult.Data.Last();
                            var startTime = DateTimeOffset.FromUnixTimeMilliseconds(firstTrade.TradeTime).ToString("yyyy-MM-dd HH:mm:ss");
                            var endTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTrade.TradeTime).ToString("yyyy-MM-dd HH:mm:ss");

                            _logger.LogInformation("[{Symbol}] [АУДИТ] Успешно заполнено {Count} сделок. (ID: {StartId}-{EndId}, Время: {StartTime} - {EndTime})",
                            symbol, fetchResult.Data.Count, firstTrade.TradeId, lastTrade.TradeId, startTime, endTime);
                            #endregion

                            currentFromId = lastTrade.TradeId + 1;

                            if (currentFromId > gapEndId) break;
                            await Task.Delay(500, token); // Вежливая пауза
                            break;

                        case FetchStatus.ApiLimit:
                            _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Задача будет повторена Hangfire.", symbol);
                            // Выбрасываем исключение, чтобы Hangfire автоматически поставил задачу на повтор с задержкой
                            throw new Exception("Binance API rate limit reached.");

                        case FetchStatus.GeneralError:
                            _logger.LogError("[{Symbol}] Ошибка API при заполнении дыры. Задача будет повторена Hangfire.", symbol);
                            throw new Exception($"Binance API general error while fetching from ID {currentFromId}.");
                    }
                }
            }
        }
        finally 
        { 
            _tracker.MarkAsCompleted(symbol, gap); 
        }
    }
}
