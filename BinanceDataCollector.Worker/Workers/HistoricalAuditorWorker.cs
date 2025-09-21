using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Hangfire.Client;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;


/// <summary>
/// Выполняет глубокий, инкрементальный аудит исторических данных.
/// Его задача - находить ВСЕ дыры в данных и ставить задачи на их исправление
/// в правильные очереди в зависимости от "возраста" дыры.
/// </summary>
[Queue("historical_audit")] // Эта задача всегда выполняется в низкоприоритетной очереди
[DisableConcurrentExecution(30 * 60)] // Даем 30 минут на обработку одной пачки символов
public class HistoricalAuditorWorker
{
    // --- Зависимости, внедренные через конструктор ---
    private readonly IHistoricalAuditRepository _auditRepo;
    private readonly ITradeRepository _tradeRepo;
    private readonly IAnalysisRepository _analysisRepo;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<HistoricalAuditorWorker> _logger;
    private readonly GapProcessingTracker _tracker;

    // --- Конфигурация ---
    private const int BatchSize = 5; // Сколько символов обрабатывать за один запуск
    private const int MaxRetries = 10;
    private readonly TimeSpan _retryInterval = TimeSpan.FromDays(1);
    private readonly TimeSpan _chunkWindow = TimeSpan.FromHours(12); // Размер "шага", которым движемся по истории

    public HistoricalAuditorWorker(
        IHistoricalAuditRepository auditRepo,
        ITradeRepository tradeRepo,
        IAnalysisRepository analysisRepo,
        IBackgroundJobClient backgroundJobClient,
        ILogger<HistoricalAuditorWorker> logger,
        GapProcessingTracker tracker)
    {
        _auditRepo = auditRepo;
        _tradeRepo = tradeRepo;
        _analysisRepo = analysisRepo;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
        _tracker = tracker;
    }

    /// <summary>
    /// Основной метод, вызываемый Hangfire. Обрабатывает одну порцию "задач" на аудит.
    /// </summary>
    public async Task AuditNextBatchAsync()
    {
        using (_logger.TimedOperation("Один цикл исторического аудита"))
        {
            try
            {
                // Получаем пачку символов, требующих проверки.
                var symbolsToAudit = await _auditRepo.GetSymbolsToAuditAsync(BatchSize, MaxRetries, _retryInterval);
                if (!symbolsToAudit.Any())
                {
                    _logger.LogInformation("Нет символов для исторического аудита в данный момент.");
                    return;
                }

                foreach (var watermark in symbolsToAudit)
                {
                    // Для каждого символа выполняем один "шаг" аудита
                    await ProcessSymbolChunkAsync(watermark);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка в HistoricalAuditorWorker");
                throw; // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            }
        }
    }

    /// <summary>
    /// Обрабатывает одну "порцию" (чанк) истории для одного символа.
    /// </summary>
    private async Task ProcessSymbolChunkAsync(HistoricalWatermark watermark)
    {
        var symbol = watermark.Symbol;

        try
        {
            // 1. Определяем границы нашего "шага" (чанка) для проверки.
            var windowStart = DateTimeOffset.FromUnixTimeMilliseconds(watermark.LastChecked_Timestamp + 1).UtcDateTime;
            var windowEnd = windowStart + _chunkWindow;

            // 2. "Защитный барьер": не лезем в "горячую" зону QuickAuditor'а.
            if (windowEnd >= DateTime.UtcNow.AddHours(-48))
            {
                _logger.LogInformation("[{Symbol}] Исторический аудит достиг 'горячей' зоны. Считаем завершенным.", symbol);
                await _auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, "Completed", false);
                return;
            }

            // 3. Находим реальные границы TradeId в этом временном окне.
            var (minId, maxId) = await _tradeRepo.GetMinMaxTradeIdInWindowAsync(symbol, windowStart, windowEnd);
            if (!minId.HasValue || !maxId.HasValue)
            {
                // В этом окне нет сделок. Просто "перепрыгиваем" его, сдвигая вотермарку.
                _logger.LogInformation("[{Symbol}] В окне {Start} - {End} нет сделок. Перепрыгиваем...", symbol, windowStart, windowEnd);
                await _auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, new DateTimeOffset(windowEnd).ToUnixTimeMilliseconds(), "Pending", false);
                return;
            }

            // 4. Ищем дыры в этом диапазоне ID с помощью быстрой SQL-функции.
            var gaps = await _analysisRepo.FindGapsInWindowAsync(symbol, minId.Value, maxId.Value);

            if (gaps.Any())
            {
                _logger.LogWarning("[{Symbol}] В диапазоне ID {MinId}-{MaxId} найдено {Count} дыр.", symbol, minId.Value, maxId.Value, gaps.Count);

                foreach (var gap in gaps)
                {
                    // 5. Для каждой дыры ставим задачу на импорт архива.
                    // Исторический аудитор ВСЕГДА использует архивы.
                    ScheduleArchiveImport(symbol, gap);
                }
            }

            // 6. Успешно сдвигаем вотермарку на конец проверенного окна.
            await _auditRepo.UpdateWatermarkAsync(symbol, maxId.Value, new DateTimeOffset(windowEnd).ToUnixTimeMilliseconds(), "Pending", false);
            _logger.LogInformation("[{Symbol}] Успешно проверено окно до {WindowEnd} (TradeId: {MaxId}).", symbol, windowEnd, maxId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Ошибка при аудите порции данных. Вотермарка не сдвинута.", symbol);
            // Обновляем с увеличением счетчика ошибок, не сдвигая вотермарку.
            await _auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, "Failed", true);
            throw;
        }
    }

    /// <summary>
    /// Ставит в очередь Hangfire задачи на скачивание архивов для закрытия дыры.
    /// </summary>
    private async void ScheduleArchiveImport(string symbol, DataGap gap)
    {
        // Определяем календарные дни, которые затрагивает дыра
        var startTrade = await _tradeRepo.GetTradeByIdAsync(gap.GapStart, symbol);
        var endTrade = await _tradeRepo.GetTradeByIdAsync(gap.GapEnd, symbol);

        // Почему появляется мусор? Откуда он берется? Влияет ли это на целостность данных? !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
        if (startTrade?.TradeTime <= 0 || endTrade?.TradeTime <= 0)
        {
            _logger.LogWarning("[{Symbol}] Не удалось найти крайние сделки для дыры {gap}. Планирование отменено.", symbol, gap);
            return;
        }

        var datesToDownload = GetDatesBetween(startTrade.TradeTime, endTrade.TradeTime);

        foreach (var date in datesToDownload)
        {
            // Используем трекер, чтобы не ставить дублирующиеся задачи
            if (_tracker.TryMarkArchiveAsProcessing(symbol, date))
            {
                _logger.LogWarning("[{Symbol}] Планируем загрузку архива за {Date} для закрытия дыры.", symbol, date);
                _backgroundJobClient.Enqueue<ArchiveImportWorker>(
                    worker => worker.ImportArchiveAsync(symbol, date, JobCancellationToken.Null)
                );
            }
        }
    }

    /// <summary>
    /// Возвращает список всех уникальных календарных дней (UTC),
    /// находящихся между двумя временными метками.
    /// </summary>
    private IEnumerable<DateOnly> GetDatesBetween(long startTimestampMs, long endTimestampMs)
    {
        long minValidTimestamp = -62135596800000;
        long maxValidTimestamp = 253402300799999;

        if (startTimestampMs < minValidTimestamp || startTimestampMs > maxValidTimestamp ||
            endTimestampMs < minValidTimestamp || endTimestampMs > maxValidTimestamp ||
            startTimestampMs > endTimestampMs)
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