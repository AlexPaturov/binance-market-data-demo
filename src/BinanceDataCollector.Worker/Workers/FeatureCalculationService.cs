using BinanceDataCollector.Worker.Common;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Гонит расчёт индикаторов следом за свечами: `sp_aggregate_dirty_minutes` пересчитала
/// свечи — они снова 'new', и процедура шлёт `NOTIFY candles_new` (миграция 010).
///
/// Сама логика расчёта — в <see cref="FeatureCalculatorWorker"/>; здесь только запуск.
/// </summary>
public sealed class FeatureCalculationService : PgNotifyConsumer
{
    private readonly IServiceScopeFactory _scopeFactory;

    protected override string Channel => "candles_new";

    public FeatureCalculationService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<FeatureCalculationService> logger)
        : base(configuration, logger)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task<int> ProcessChunkAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var calculator = scope.ServiceProvider.GetRequiredService<FeatureCalculatorWorker>();

        return await calculator.ProcessNextBatchAsync(stoppingToken);
    }
}
