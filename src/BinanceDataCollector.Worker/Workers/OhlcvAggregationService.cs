using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Агрегирует сырые тики из `Trades` в минутные свечи `Ohlcv_1min`.
///
/// Работу находит очередь `DirtyMinutes`: минуту в неё ставит сама вставка тиков, она же
/// шлёт `NOTIFY dirty_minutes` (миграция 010). Что бы ни приехало и в каком бы порядке —
/// закрытие дыры, архив «позади» уже посчитанного участка — минута помечается грязной
/// и свеча пересчитывается.
///
/// Свеча пересчитывается целиком из всех тиков минуты, поэтому повторный прогон
/// на тех же данных даёт тот же результат.
/// </summary>
public sealed class OhlcvAggregationService : PgNotifyConsumer
{
    /// <summary>
    /// Сколько минут разбирать за один вызов процедуры. Вызов — отдельная транзакция:
    /// сколько разобрано, столько и закоммичено, обрыв откатывает только текущий кусок.
    ///
    /// Размер — компромисс между ценой вызова и ценой отката. Замеры на проде под импортом
    /// архивов (16.07.2026): кусок в 250 минут разбирается ~60 с, в 2 500 — ~271 с. Отсюда
    /// цена вызова ≈ 37 с фиксированно плюс ≈ 0.09 с на минуту: разбор НЕ линеен по числу
    /// минут, у вызова есть заметная постоянная часть, и на мелких кусках она съедает
    /// больше половины времени. При 250 минутах это стоило двукратной пропускной
    /// способности против прежней пачки в 2 500 (250 минут/мин против ~500–680).
    ///
    /// 1 000 минут — это ~130 с на вызов при обычной нагрузке и ~260 с в худшем
    /// наблюдавшемся случае (импорт насыщает диск, цена минуты растёт до ~0.22 с). Даже
    /// худший случай вдвое ниже командного таймаута 600 с, поэтому в стену, из-за которой
    /// вся переделка и затевалась, кусок не упирается.
    /// </summary>
    private const int ChunkMinutes = 1_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OhlcvAggregationService> _logger;

    protected override string Channel => "dirty_minutes";

    public OhlcvAggregationService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<OhlcvAggregationService> logger)
        : base(configuration, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task<int> ProcessChunkAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var tradeRepository = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        var minutes = await tradeRepository.AggregateDirtyMinutesAsync(ChunkMinutes);

        if (minutes > 0)
        {
            _logger.LogInformation("Aggregated {Count} minutes from the dirty minute queue.", minutes);
        }

        return minutes;
    }
}
