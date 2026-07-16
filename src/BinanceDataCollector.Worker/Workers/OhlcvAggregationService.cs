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
    /// Размер выбран с запасом под командный таймаут 600 с: пачка в 2 500 минут на фоне
    /// импорта архивов доходила до 582 с (замер 16.07.2026) — впритык к откату. Куски
    /// по 250 минут читают около 47 МБ (замер LATERAL-скана, миграция 007) и на порядок
    /// не дотягивают до таймаута. Пропускная способность от дробления не страдает:
    /// стоимость разбора линейна по числу минут.
    /// </summary>
    private const int ChunkMinutes = 250;

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
