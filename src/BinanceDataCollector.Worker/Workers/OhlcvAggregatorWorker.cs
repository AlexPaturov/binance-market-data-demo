using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Агрегирует сырые тики из `Trades` в минутные свечи `Ohlcv_1min`.
///
/// Работа идёт от СТАТУСА тиков, а не от watermark'а по времени. Раньше агрегатор шёл
/// вперёд фиксированными окнами от watermark'а, и всё, что вставлялось «позади» уже
/// пройденной отметки, не агрегировалось никогда: закрытая дыра не попадала в свечи,
/// а архивы, приехавшие вразнобой, терялись. Теперь окно начинается с самого старого
/// необработанного тика — что бы ни приехало и в каком бы порядке, оно будет учтено.
///
/// Свеча пересчитывается целиком из всех тиков минуты, поэтому повторный прогон
/// на тех же данных даёт тот же результат.
/// </summary>
[Queue("historical_audit")]
[DisableConcurrentExecution(30 * 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class OhlcvAggregatorWorker
{
    /// <summary>
    /// Ширина окна пересчёта за один цикл. Ограничивает объём работы: при массовом
    /// импорте необработанных тиков могут быть сотни миллионов.
    /// </summary>
    private static readonly TimeSpan WindowSize = TimeSpan.FromHours(6);

    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<OhlcvAggregatorWorker> _logger;

    public OhlcvAggregatorWorker(
        ITradeRepository tradeRepository,
        ILogger<OhlcvAggregatorWorker> logger)
    {
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    public async Task AggregateNextBatchAsync()
    {
        using (_logger.TimedOperation("OHLCV aggregation batch"))
        {
            var candles = await _tradeRepository.AggregateNewTradesAsync(
                (long)WindowSize.TotalMilliseconds);

            if (candles == 0)
            {
                _logger.LogInformation("No unprocessed trades. Nothing to aggregate.");
                return;
            }

            _logger.LogInformation("Aggregated {Count} candles from unprocessed trades.", candles);
        }
    }
}
