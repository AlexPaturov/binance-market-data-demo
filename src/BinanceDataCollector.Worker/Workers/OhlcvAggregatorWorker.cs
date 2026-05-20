using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически агрегирует сырые сделки (тиковые данные)
/// из таблицы Trades в минутные свечи (OHLCV) в таблице Ohlcv_1min.
/// </summary>

[Queue("historical_audit")]
[DisableConcurrentExecution(15 * 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class OhlcvAggregatorWorker
{
    private readonly ITradeRepository _tradeRepository;
    private readonly IAuditRepository _auditRepository;
    private readonly ILogger<OhlcvAggregatorWorker> _logger;
    private readonly TimeSpan _windowSize = TimeSpan.FromMinutes(15);

    public OhlcvAggregatorWorker(
        ITradeRepository tradeRepository,
        IAuditRepository auditRepository,
        ILogger<OhlcvAggregatorWorker> logger
    ) 
    {
        _tradeRepository = tradeRepository;
        _auditRepository = auditRepository;
        _logger = logger;
    }

    public async Task AggregateNextBatchAsync() // Новое имя
    {
        using (_logger.TimedOperation("OHLCV aggregation batch"))
        {
            try
            {
                // 1. Получаем вотермарку - с какого момента начинать
                var watermark = await _auditRepository.GetAggregationWatermarkAsync();
                if (watermark is null)
                {
                    _logger.LogCritical("Watermark 'OhlcvAggregator' not found in Processing_Watermarks. Aggregation is impossible. Manual intervention required: insert the row or run initialization.");
                    throw new InvalidOperationException("Watermark 'OhlcvAggregator' not found in Processing_Watermarks.");
                }

                if (watermark.Status == "Completed")
                {
                    _logger.LogInformation("All trades aggregated. Nothing to do.");
                    return;
                }

                long startTimestamp = watermark.LastProcessedTimestamp + 1;
                long endTimestamp = startTimestamp + (long)_windowSize.TotalMilliseconds;

                // 2. Проверяем, не залезли ли мы в "горячую" зону
                var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
                if (startTimestamp >= oneHourAgo)
                {
                    _logger.LogInformation("Aggregation reached the hot zone. Skipping cycle.");
                    await _auditRepository.UpdateAggregationWatermarkAsync(startTimestamp - 1, "Completed");
                    return;
                }
                
                await _tradeRepository.ExecuteAggregationAsync(startTimestamp, endTimestamp);       // 3. Вызываем процедуру для обработки ОДНОГО окна
                await _auditRepository.UpdateAggregationWatermarkAsync(endTimestamp, "Pending");    // 4. Сдвигаем вотермарку вперед
                #region LogInformation
                _logger.LogInformation(
                      "Aggregated trades in window {StartTime} - {EndTime}",
                      DateTimeOffset.FromUnixTimeMilliseconds(startTimestamp).ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                      DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp).ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                  );
                #endregion
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error aggregating OHLCV batch.");
                throw;
            }
        }
    }
}
