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
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OhlcvAggregatorWorker> _logger;
    private readonly TimeSpan _windowSize = TimeSpan.FromDays(1); // Обрабатываем по 1 дню за раз

    public OhlcvAggregatorWorker(
        IServiceProvider serviceProvider,
        ILogger<OhlcvAggregatorWorker> logger
    ) 
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task AggregateNextBatchAsync() // Новое имя
    {
        using (_logger.TimedOperation("Плановая агрегация свечей (одна порция)"))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
                var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>(); // Нужен репозиторий для вотермарок

                // 1. Получаем вотермарку - с какого момента начинать
                var watermark = await auditRepo.GetAggregationWatermarkAsync(); // <-- Новый метод в репозитории
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
                    await auditRepo.UpdateAggregationWatermarkAsync(startTimestamp - 1, "Completed");
                    return;
                }

                // 3. Вызываем "глупую" процедуру для обработки ОДНОГО окна
                await tradeRepo.ExecuteAggregationAsync(startTimestamp, endTimestamp);

                // 4. Сдвигаем вотермарку вперед
                await auditRepo.UpdateAggregationWatermarkAsync(endTimestamp, "Pending");

                _logger.LogInformation("Успешно агрегированы сделки в окне до {EndTime}",
                    DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при агрегации порции свечей.");
                throw;
            }
        }
    }
}
