using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Агрегирует сырые тики из `Trades` в минутные свечи `Ohlcv_1min`.
///
/// Работу находит очередь `DirtyMinutes`: минуту в неё ставит сама вставка тиков.
/// Что бы ни приехало и в каком бы порядке — закрытие дыры, архив «позади» уже
/// посчитанного участка — минута помечается грязной и свеча пересчитывается.
///
/// Свеча пересчитывается целиком из всех тиков минуты, поэтому повторный прогон
/// на тех же данных даёт тот же результат.
/// </summary>
// Очередь приоритетного сервера: свечи — основа графика, индикаторов и фич, они нужны
// в темпе поступления тиков. На фоновом сервере агрегатор делил воркеров с импортом
// архивов и вставал вместе с ним: пачка распаковки занимала всех воркеров, свечи
// переставали считаться на часы.
[Queue("realtime")]
[SkipWhenPreviousJobIsRunning]
[DisableConcurrentExecution(30 * 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class OhlcvAggregatorWorker
{
    /// <summary>
    /// Сколько минут разбирать за один цикл. Ограничивает объём работы: после импорта
    /// архивов в очереди могут стоять сотни тысяч минут, и проход обязан укладываться
    /// в командный таймаут — иначе транзакция откатится и работа не сдвинется вовсе.
    ///
    /// Размер подобран под худший случай — параллельный импорт архивов, когда диск
    /// делят вставка, агрегация и autovacuum: 10 000 минут в этих условиях читались
    /// дольше 600-секундного таймаута и половина пачек сгорала в откатах (ночь на
    /// 14.07.2026); 2 500 коммитятся за 2–3 минуты стабильно.
    /// </summary>
    private const int BatchMinutes = 2_500;

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
            var candles = await _tradeRepository.AggregateDirtyMinutesAsync(BatchMinutes);

            if (candles == 0)
            {
                _logger.LogInformation("Dirty minute queue is empty. Nothing to aggregate.");
                return;
            }

            _logger.LogInformation("Aggregated {Count} candles from the dirty minute queue.", candles);
        }
    }
}
