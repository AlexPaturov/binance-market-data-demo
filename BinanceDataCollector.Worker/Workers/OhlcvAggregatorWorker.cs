using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который периодически агрегирует сырые сделки (тиковые данные)
/// из таблицы Trades в минутные свечи (OHLCV) в таблице Ohlcv_1min.
/// </summary>

[Queue("historical_audit")] // <-- СТАВИМ В МЕНЕЕ ПРИОРИТЕТНУЮ ОЧЕРЕДЬ
[DisableConcurrentExecution(15 * 60)]
public class OhlcvAggregatorWorker
{
    private readonly ITradeRepository _tradeRepository;
    private readonly IAuditRepository _auditRepository;
    private readonly ILogger<OhlcvAggregatorWorker> _logger;
    private readonly TimeSpan _windowSize = TimeSpan.FromHours(1); // Обрабатываем по 2 часа за раз

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
        using (_logger.TimedOperation("Плановая агрегация свечей (одна порция)"))
        {
            try
            {
                // 1. Получаем вотермарку - с какого момента начинать
                var watermark = await _auditRepository.GetAggregationWatermarkAsync(); // <-- Новый метод в репозитории
                if (watermark.Status == "Completed")
                {
                    _logger.LogInformation("Агрегация всех сделок завершена.");
                    return;
                }

                long startTimestamp = watermark.LastProcessedTimestamp + 1;
                long endTimestamp = startTimestamp + (long)_windowSize.TotalMilliseconds;

                // 2. Проверяем, не залезли ли мы в "горячую" зону
                var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
                if (startTimestamp >= oneHourAgo)
                {
                    _logger.LogInformation("Агрегация достигла 'горячей' зоны. Пропускаем цикл.");
                    await _auditRepository.UpdateAggregationWatermarkAsync(startTimestamp - 1, "Completed");
                    return;
                }
                
                await _tradeRepository.ExecuteAggregationAsync(startTimestamp, endTimestamp);       // 3. Вызываем процедуру для обработки ОДНОГО окна
                await _auditRepository.UpdateAggregationWatermarkAsync(endTimestamp, "Pending");    // 4. Сдвигаем вотермарку вперед
                #region LogInformation
                _logger.LogInformation(
                      "Успешно агрегированы сделки в окне с {StartTime} по {EndTime}",
                      DateTimeOffset.FromUnixTimeMilliseconds(startTimestamp).ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                      DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp).ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                  );
                #endregion
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при агрегации порции свечей.");
                throw;
            }
        }
    }
}
