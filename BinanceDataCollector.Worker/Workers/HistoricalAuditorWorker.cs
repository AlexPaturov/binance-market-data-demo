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
    private readonly IHistoricalAuditRepository _auditRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly IAnalysisRepository _analysisRepository;

    private readonly ILogger<HistoricalAuditorWorker> _logger;

    // --- Конфигурация ---
    private readonly TimeSpan _retryInterval = TimeSpan.FromDays(1);
    private const int BatchSize = 10; // Сколько символов обрабатывать за один цикл
    private const int MaxRetries = 10;
    private readonly TimeSpan _windowSize = TimeSpan.FromDays(1);
    //private readonly TimeSpan _chunkWindow = TimeSpan.FromHours(1); // Определяем размер "шага", которым мы будем двигаться по истории

    public HistoricalAuditorWorker(
        ILogger<HistoricalAuditorWorker> logger,
        IHistoricalAuditRepository auditRepository,
        ITradeRepository tradeRepository,
        IAnalysisRepository analysisRepository
    )
    {
        _logger = logger;
        _auditRepository = auditRepository;
        _tradeRepository = tradeRepository;
        _analysisRepository = analysisRepository;
    }

    [Queue("historical_audit")]
    public async Task AuditNextBatchAsync()
    {
        _logger.LogInformation("--- Начинаем исторический аудит ---");
        using (_logger.TimedOperation("Один цикл исторического аудита"))
        {
            try
            {
                await _auditRepository.InitializeAuditForNewSymbolsAsync(); // Эта логика может быть в отдельном воркере, как мы обсуждали

                var symbolsToAudit = await _auditRepository.GetSymbolsToAuditAsync(BatchSize, MaxRetries, _retryInterval);
                if (!symbolsToAudit.Any())
                {
                    _logger.LogInformation("Нет символов для исторического аудита в данный момент.");
                    return; // Просто выходим
                }

                foreach (var watermark in symbolsToAudit)
                {
                    await ProcessSymbolAuditAsync(watermark, CancellationToken.None); // Передаем CancellationToken.None, т.к. Hangfire сам управляет таймаутами
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

    private async Task ProcessSymbolAuditAsync(HistoricalWatermark watermark, CancellationToken stoppingToken)
    {
        string symbol = watermark.Symbol;
        long startTradeId = watermark.LastChecked_TradeId + 1;

        using (_logger.TimedOperation("Аудит символа [{Symbol}] с TradeId {StartId}", symbol, startTradeId))
        {
            try
            {
                // 3. Определяем конец окна для проверки
                long endTimestamp = watermark.LastChecked_Timestamp + (long)_windowSize.TotalMilliseconds;
                long? endTradeId = await _tradeRepository.GetLastTradeIdBeforeTimestampAsync(symbol, endTimestamp);

                // Случай 1: В 3-дневном окне нет новых сделок
                if (!endTradeId.HasValue || endTradeId.Value <= startTradeId)
                {
                    var lastTradeInDb = await _tradeRepository.GetLastTradeAsync(symbol);
                    if (lastTradeInDb != null && lastTradeInDb.TradeTime > endTimestamp)
                    {
                        // Сделки есть, но они далеко в будущем. "Перепрыгиваем" пустое окно.
                        _logger.LogInformation("[{Symbol}] В окне до {EndTime} нет сделок. Перепрыгиваем...", symbol, DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp));
                        await _auditRepository.UpdateWatermarkAsync(symbol, lastTradeInDb.TradeId, lastTradeInDb.TradeTime, "Pending", false);
                    }
                    else
                    {
                        // Мы дошли до конца истории. Считаем аудит завершенным.
                        _logger.LogInformation("[{Symbol}] Достигнут конец истории. Аудит завершен.", symbol);
                        await _auditRepository.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, "Completed", false);
                    }
                    return;
                }

                // 4. Ищем дыры в определенном окне
                var tradeIdsInWindow = await _tradeRepository.GetTradeIdsInWindowAsync(symbol, startTradeId, endTradeId.Value);
                var gaps = await _analysisRepository.FindGapsInWindowAsync(symbol, startTradeId, endTradeId.Value);
                bool allGapsFilled = true;

                if (gaps.Any())
                {
                    _logger.LogWarning("[{Symbol}] В диапазоне ID {StartId}-{EndId} найдено {Count} дыр.", symbol, startTradeId, endTradeId.Value, gaps.Count);
                    
                    foreach (var gap in gaps)
                    {
                        // 2. Определяем, какие календарные дни (UTC) затрагивает эта дыра.
                        var startTrade = await _tradeRepository.GetTradeByIdAsync(gap.GapStart, symbol);
                        var endTrade = await _tradeRepository.GetTradeByIdAsync(gap.GapEnd, symbol);

                        #region ДЛЯ ОТЛАДКИ 
                        if (startTrade == null || endTrade == null)
                        {
                            _logger.LogWarning("[{Symbol}] Не удалось найти крайние сделки для дыры {StartId}-{EndId}", symbol, gap.GapStart, gap.GapEnd);
                            continue;
                        }
                        _logger.LogDebug("[{Symbol}] Проверяем дыру между сделками: StartTradeId={StartId}, StartTradeTime={StartTime}, EndTradeId={EndId}, EndTradeTime={EndTime}",
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
                    await _auditRepository.UpdateWatermarkAsync(symbol, endTradeId.Value, endTimestamp, "Pending", false);
                    _logger.LogInformation("[{Symbol}] Успешно проверен диапазон до TradeId {EndId}. Вотермарка сдвинута.", symbol, endTradeId.Value);
                }
                else
                {
                    var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                    await _auditRepository.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
                    _logger.LogError("[{Symbol}] Не удалось заполнить дыры в диапазоне {StartId}-{EndId}. Попытка #{RetryCount}",
                        symbol, startTradeId, endTradeId.Value, watermark.RetryCount + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Symbol}] Критическая ошибка при аудите диапазона, начиная с TradeId {StartId}.", symbol, startTradeId);
                var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                await _auditRepository.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
            }
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
            _logger.LogWarning("Получены некорректные временные метки для GetDatesBetween: Start={start}, End={end}. Пропускаем.", startTimestampMs, endTimestampMs);
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