using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Сервис, который проверяет целостность тиковых данных в таблице Trades и автоматически заполняет небольшие пробелы.
/// </summary>
[Queue("quick_audit")]
[DisableConcurrentExecution(15 * 60)]
public class QuickAuditorWorker
{
    private readonly GapProcessingTracker _tracker;
    private readonly ITrackedSymbolRepository _trackedSymbolRepository;
    private readonly ITradeRepository _tradeRepository;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<QuickAuditorWorker> _logger;
    private readonly TimeSpan _totalWindow = TimeSpan.FromHours(24);
    private readonly TimeSpan _chunkWindow = TimeSpan.FromHours(1); // Проверяем по 1 часу

    public QuickAuditorWorker(
        GapProcessingTracker tracker,
        ITrackedSymbolRepository trackedSymbolRepository,
        ITradeRepository tradeRepository,
        IBackgroundJobClient backgroundJobClient,
        ILogger<QuickAuditorWorker> logger
        )
    {
        _tracker = tracker;
        _trackedSymbolRepository = trackedSymbolRepository;
        _tradeRepository = tradeRepository;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task CheckAndFillRecentGapsAsync()
    {
        _logger.LogInformation("--- Начинаем быстрый аудит за последние {TotalHours} часов ---", _totalWindow.TotalHours);

        var activeSymbols = await _trackedSymbolRepository.GetActiveSymbolsAsync();

        foreach (var symbol in activeSymbols)
        {
            var windowEnd = DateTime.UtcNow;
            var windowStart = windowEnd - _chunkWindow;

            // В цикле идем "назад" во времени маленькими шагами
            while (windowStart > DateTime.UtcNow - _totalWindow)
            {
                _logger.LogDebug("[{Symbol}] Проверяем часовое окно: {Start} -> {End}", symbol, windowStart, windowEnd);

                // 1. Ищем дыры в МАЛЕНЬКОМ окне
                var gaps = await _tradeRepository.FindGapsInTimeWindowAsync(symbol, windowStart, windowEnd);

                if (gaps.Any())
                {
                    _logger.LogWarning("[{Symbol}] В окне [{Start}] - [{End}] найдено {Count} дыр.", 
                        symbol, windowStart.ToString("yyyy-MM-dd HH:mm:ss"), windowEnd.ToString("yyyy-MM-dd HH:mm:ss"), gaps.Count);
                    foreach (var gap in gaps)
                    {
                        // 2. Ставим задачи на заполнение
                        if (_tracker.TryMarkAsProcessing(symbol, gap))
                        {
                            // Если удалось пометить - значит, это новая дыра. Ставим задачу.
                            _backgroundJobClient.Enqueue<FillGapWorker>(
                                x => x.FillWithApiAsync(symbol, gap, JobCancellationToken.Null)
                            );
                        }
                        else
                        {
                            // Эта дыра уже в обработке, игнорируем.
                            _logger.LogDebug("[{Symbol}] Дыра {gap} уже находится в обработке. Пропускаем.", symbol, gap);
                        }
                    }
                }

                // Сдвигаем окно назад на 1 час
                windowEnd = windowStart;
                windowStart = windowEnd - _chunkWindow;
            }
        }
    }
}
