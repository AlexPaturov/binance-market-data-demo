using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

[Queue("quick_audit")] // Это быстрая, приоритетная задача
[DisableConcurrentExecution(10 * 60)]
public class KlinesDownloaderWorker
{
    // --- Зависимости, внедренные через конструктор ---
    private readonly GapProcessingTracker _tracker;
    private readonly ITrackedSymbolRepository _symbolRepo;
    private readonly IOhlcvRepository _ohlcvRepo;
    private readonly IBinanceService _binanceService;
    private readonly ILogger<KlinesDownloaderWorker> _logger;

    public KlinesDownloaderWorker(
        ITrackedSymbolRepository symbolRepo,
        IOhlcvRepository ohlcvRepo,
        IBinanceService binanceService,
        ILogger<KlinesDownloaderWorker> logger,
        GapProcessingTracker tracker)
    {
        _symbolRepo = symbolRepo;
        _ohlcvRepo = ohlcvRepo;
        _binanceService = binanceService;
        _logger = logger;
        _tracker = tracker;
    }

    /// <summary>
    /// Основной метод. Скачивает недостающие свечи для ВСЕХ активных символов.
    /// </summary>
    public async Task DownloadKlinesAsync()
    {
        using (_logger.TimedOperation("Scheduled klines download"))
        {
            var activeSymbols = await _symbolRepo.GetActiveSymbolsAsync();

            foreach (var symbol in activeSymbols)
            {
                try
                {
                    await DownloadKlinesForSymbolAsync(symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Symbol}] Error downloading klines.", symbol);
                    // Не перевыбрасываем, чтобы ошибка одного символа не остановила весь процесс
                }
            }
        }
    }

    /// <summary>
    /// Скачивает недостающие свечи для ОДНОГО символа, используя вотермарку.
    /// </summary>
    private async Task DownloadKlinesForSymbolAsync(string symbol)
    {
        // ====================================================================
        // ===== ВОТ ОН, НАШ МЕХАНИЗМ ВОТЕРМАРКИ! =====
        // ====================================================================

        // 1. Определяем, с какого момента начинать загрузку,
        //    запросив время последней УЖЕ сохраненной свечи.
        var lastOpenTimeMs = await _ohlcvRepo.GetLastKlineOpenTimeAsync(symbol);

        DateTime startTime;

        if (lastOpenTimeMs.HasValue)
        {
            // Если данные уже есть, начинаем со следующей минуты после последней сохраненной.
            startTime = DateTimeOffset.FromUnixTimeMilliseconds(lastOpenTimeMs.Value).UtcDateTime.AddMinutes(1);
        }
        else
        {
            // Если для символа еще нет свечей, начинаем с разумной даты в прошлом.
            // 3 года - слишком много и создаст огромную задачу. Начнем с 30 дней.
            // Глубокую историю заполнит HistoricalAuditor.
            startTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _logger.LogWarning("[{Symbol}] No klines in database. Starting download from last 30 days.", symbol);
        }

        // Загружаем данные до текущего момента.
        var endTime = DateTime.UtcNow;

        // Если startTime уже в будущем (например, из-за рассинхронизации часов),
        // или если последняя свеча - это текущая минута, то выходим.
        if (startTime >= endTime)
        {
            _logger.LogInformation("[{Symbol}] Klines are up to date. Skipping.", symbol);
            return;
        }

        // ====================================================================
        // ===== ЗАЩИТА ОТ ДУБЛИРОВАНИЯ =====
        // ====================================================================

        // Пытаемся "зарезервировать" эту работу
        if (!_tracker.TryMarkKlinesAsProcessing(symbol, startTime))
        {
            _logger.LogDebug("[{Symbol}] Klines download from {Start} already in progress. Skipping.", symbol, startTime);
            return; // Если не удалось - выходим, работа уже ведется.
        }

        // ====================================================================

        // 2. Вызываем сервис для загрузки нужного диапазона
        var klines = (await _binanceService.GetHistoricalKlinesAsync(symbol, startTime, endTime, CancellationToken.None)).ToList();

        try
        {
            if (klines.Any())
            {
                // 3. Сохраняем скачанные свечи в базу
                await _ohlcvRepo.BulkUpsertAsync(klines);
                _logger.LogInformation("[{Symbol}] Successfully downloaded and saved {Count} new klines.", symbol, klines.Count);
            }
            else
            {
                _logger.LogInformation("[{Symbol}] No new klines found in range {Start} - {End}.", symbol, startTime, endTime);
            }
        }
        finally
        {
            // 4. В любом случае "снимаем резерв"
            _tracker.MarkKlinesAsCompleted(symbol, startTime);
        }
    }
}