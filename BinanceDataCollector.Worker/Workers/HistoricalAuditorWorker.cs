using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire.Client;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;
/// <summary>
/// Выполняет глубокий, инкрементальный аудит исторических данных,
/// проверяя целостность последовательности TradeId небольшими окнами.
/// </summary>
//
public class HistoricalAuditorWorker
{
    private readonly ILogger<HistoricalAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // --- Конфигурация ---
    private readonly TimeSpan _retryInterval = TimeSpan.FromDays(1);
    private const int BatchSize = 5; // Сколько символов обрабатывать за один цикл
    private const int MaxRetries = 10;
    private readonly TimeSpan _windowSize = TimeSpan.FromDays(3);

    public HistoricalAuditorWorker(
        ILogger<HistoricalAuditorWorker> logger,
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    [Queue("historical_audit")]
    public async Task AuditNextBatchAsync()
    {
        _logger.LogInformation("--- Начинаем исторический аудит ---");
        using (_logger.TimedOperation("Один цикл исторического аудита"))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var auditRepo = scope.ServiceProvider.GetRequiredService<IHistoricalAuditRepository>();
                await auditRepo.InitializeAuditForNewSymbolsAsync(); // Эта логика может быть в отдельном воркере, как мы обсуждали

                var symbolsToAudit = await auditRepo.GetSymbolsToAuditAsync(BatchSize, MaxRetries, _retryInterval);
                if (!symbolsToAudit.Any())
                {
                    _logger.LogInformation("Нет символов для исторического аудита в данный момент.");
                    return; // Просто выходим
                }

                foreach (var watermark in symbolsToAudit)
                {
                    await ProcessSymbolAuditAsync(scope, watermark, CancellationToken.None); // Передаем CancellationToken.None, т.к. Hangfire сам управляет таймаутами
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка - HistoricalAuditorWorker");
                throw;  // 5. Перевыбрасываем исключение, чтобы Hangfire пометил задачу как Failed
            }
        }
        _logger.LogInformation("--- Исторический аудит закончен ---");
    }

    private async Task ProcessSymbolAuditAsync(IServiceScope scope, HistoricalWatermark watermark, CancellationToken stoppingToken)
    {
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IHistoricalAuditRepository>();

        string symbol = watermark.Symbol;
        long startTradeId = watermark.LastChecked_TradeId + 1;

        using (_logger.TimedOperation("Аудит символа [{Symbol}] с TradeId {StartId}", symbol, startTradeId))
        {
            try
            {
                // 3. Определяем конец окна для проверки
                long endTimestamp = watermark.LastChecked_Timestamp + (long)_windowSize.TotalMilliseconds;
                long? endTradeId = await tradeRepo.GetLastTradeIdBeforeTimestampAsync(symbol, endTimestamp);

                // Случай 1: В 3-дневном окне нет новых сделок
                if (!endTradeId.HasValue || endTradeId.Value <= startTradeId)
                {
                    var lastTradeInDb = await tradeRepo.GetLastTradeAsync(symbol);
                    if (lastTradeInDb != null && lastTradeInDb.TradeTime > endTimestamp)
                    {
                        // Сделки есть, но они далеко в будущем. "Перепрыгиваем" пустое окно.
                        _logger.LogInformation("[{Symbol}] В окне до {EndTime} нет сделок. Перепрыгиваем...", symbol, DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp));
                        await auditRepo.UpdateWatermarkAsync(symbol, lastTradeInDb.TradeId, lastTradeInDb.TradeTime, "Pending", false);
                    }
                    else
                    {
                        // Мы дошли до конца истории. Считаем аудит завершенным.
                        _logger.LogInformation("[{Symbol}] Достигнут конец истории. Аудит завершен.", symbol);
                        await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, "Completed", false);
                    }
                    return;
                }

                // 4. Ищем дыры в определенном окне
                var tradeIdsInWindow = await tradeRepo.GetTradeIdsInWindowAsync(symbol, startTradeId, endTradeId.Value);
                var gaps = await analysisRepo.FindGapsInWindowAsync(symbol, startTradeId, endTradeId.Value);
                bool allGapsFilled = true;

                if (gaps.Any())
                {
                    _logger.LogWarning("[{Symbol}] В диапазоне ID {StartId}-{EndId} найдено {Count} дыр.", symbol, startTradeId, endTradeId.Value, gaps.Count);
                    
                    foreach (var gap in gaps)
                    {
                        // 2. Определяем, какие календарные дни (UTC) затрагивает эта дыра.
                        var startTrade = await tradeRepo.GetTradeByIdAsync(gap.GapStart, symbol);
                        var endTrade = await tradeRepo.GetTradeByIdAsync(gap.GapEnd, symbol);

                        #region ДЛЯ ОТЛАДКИ 
                        if (startTrade == null || endTrade == null)
                        {
                            _logger.LogWarning("[{Symbol}] Не удалось найти крайние сделки для дыры {StartId}-{EndId}",
                                symbol, gap.GapStart, gap.GapEnd);
                            continue;
                        }
                        _logger.LogDebug("[{Symbol}] Проверяем дыру между сделками: StartTradeId={startId}, StartTradeTime={startTime}, EndTradeId={endId}, EndTradeTime={endTime}",
                        symbol, startTrade.TradeId, startTrade.TradeTime, endTrade.TradeId, endTrade.TradeTime);
                        #endregion

                        var twentyFourHoursAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-24)); // пропускаем всё что раньше 24 часов от настоящей даты
                        var datesToDownload = GetDatesBetween(startTrade.TradeTime, endTrade.TradeTime);

                        // 3. Ставим задачи в Hangfire для нового воркера.
                        foreach (var date in datesToDownload)
                        {
                            if (date >= twentyFourHoursAgo) 
                            {
                                _logger.LogInformation("[{Symbol}] Пропускаем планирование для даты {Date}, т.к. это 'горячая' зона QuickAuditor.", symbol, date);
                                continue; // Переходим к следующей дате
                            }

                            _logger.LogWarning("Планируем загрузку архива для {Symbol} за {Date}", symbol, date);
                            BackgroundJob.Enqueue<ArchiveImportWorker>(
                                worker => worker.ImportArchiveAsync(symbol, date, CancellationToken.None)
                            );
                        }
                    }

                }
                else
                {
                    _logger.LogInformation("[{Symbol}] Дыр в диапазоне ID {StartId}-{EndId} не найдено.", symbol, startTradeId, endTradeId.Value);
                }

                // 5. Обновляем вотермарку
                if (allGapsFilled)
                {
                    await auditRepo.UpdateWatermarkAsync(symbol, endTradeId.Value, endTimestamp, "Pending", false);
                    _logger.LogInformation("[{Symbol}] Успешно проверен диапазон до TradeId {EndId}. Вотермарка сдвинута.", symbol, endTradeId.Value);
                }
                else
                {
                    var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                    await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
                    _logger.LogError("[{Symbol}] Не удалось заполнить дыры в диапазоне {StartId}-{EndId}. Попытка #{RetryCount}",
                        symbol, startTradeId, endTradeId.Value, watermark.RetryCount + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Symbol}] Критическая ошибка при аудите диапазона, начиная с TradeId {StartId}.", symbol, startTradeId);
                var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
            }
        }
    }

    private async Task<bool> FillGapAsync(IServiceScope scope, string symbol, DataGap gap, CancellationToken stoppingToken)
    {
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        long currentFromId = gap.GapStart + 1;
        long gapEndId = gap.GapEnd - 1;
        long tradesToFetch = gapEndId - currentFromId + 1;

        if (tradesToFetch <= 0) return true;

        using (_logger.TimedOperation(LogLevel.Warning, "[{Symbol}] Заполнение дыры в {Count} сделок с ID {StartId} по {EndId}", symbol, tradesToFetch, currentFromId, gapEndId))
        {
            while (currentFromId <= gapEndId && !stoppingToken.IsCancellationRequested)
            {
                long remainingTrades = gapEndId - currentFromId + 1;
                int currentLimit = (int)Math.Min(remainingTrades, 1000);

                // Используем новый метод для получения СЫРЫХ сделок
                var fetchResult = await binanceService.GetHistoricalRawTradesAsync(symbol, currentFromId, stoppingToken, currentLimit);

                switch (fetchResult.Status)
                {
                    case FetchStatus.Success:
                        if (!fetchResult.Data.Any())
                        {
                            _logger.LogError("[{Symbol}] Binance не вернул данные для ID >= {FromId}, хотя должен был. Заполнение дыры прервано.", symbol, currentFromId);
                            return false;
                        }

                        await tradeRepo.BulkInsertAsync(fetchResult.Data);
                        var lastFilledTrade = fetchResult.Data.Last();
                        currentFromId = lastFilledTrade.TradeId + 1;

                        if (currentFromId > gapEndId) break;
                        await Task.Delay(500, stoppingToken);
                        break;

                    case FetchStatus.ApiLimit:
                        _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Засыпаем на 5 минут...", symbol);
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        break;

                    case FetchStatus.GeneralError:
                        _logger.LogError("[{Symbol}] Ошибка API при заполнении дыры. Прекращаем попытки.", symbol);
                        return false;
                }
            }

            return !stoppingToken.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Возвращает список всех уникальных календарных дней (UTC),
    /// находящихся между двумя временными метками.
    /// </summary>
    private IEnumerable<DateOnly> GetDatesBetween(long startTimestampMs, long endTimestampMs)
    {
        // ===== ЗАЩИТНАЯ ПРОВЕРКА =====
        if (startTimestampMs <= 0 || endTimestampMs <= 0 || startTimestampMs > endTimestampMs)
        {
            // Если временные метки некорректны или перепутаны,
            // возвращаем пустую коллекцию, чтобы избежать ошибки.
            _logger.LogWarning("Получены некорректные временные метки для GetDatesBetween: Start={start}, End={end}. Пропускаем.",
                startTimestampMs, endTimestampMs);
            yield break;
        }
        // =============================

        var startDate = DateTimeOffset.FromUnixTimeMilliseconds(startTimestampMs).UtcDateTime.Date;
        var endDate = DateTimeOffset.FromUnixTimeMilliseconds(endTimestampMs).UtcDateTime.Date;

        var currentDate = startDate;
        while (currentDate <= endDate)
        {
            yield return DateOnly.FromDateTime(currentDate);
            currentDate = currentDate.AddDays(1);
        }
    }
}