using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using System.Diagnostics;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Регистрирует периодические задачи.
///
/// Рабочий режим: данные идут с WebSocket (`BinanceCollectorWorker`), агрегируются в свечи,
/// по свечам считаются индикаторы, аудиторы закрывают дыры от обрывов связи, ротация
/// следит за размером диска. Импорт архивов — бутстрап истории и восстановление после
/// долгого простоя, запускается вручную со страницы Archive.
///
/// Агрегатор безопасно работает параллельно с любой докачкой: он идёт от статуса тиков,
/// а не от watermark'а по времени, поэтому данные, приехавшие «позади», не теряются
/// (см. docs/adr/0004-watermarking-idempotency.md). Раньше это было не так, и на время
/// загрузки его приходилось выключать.
/// </summary>
public class HangfireJobsService : IHostedService
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HangfireJobsService> _logger;
    private readonly bool _isDevelopment;

    public HangfireJobsService(
        IRecurringJobManager recurringJobManager,
        IServiceScopeFactory scopeFactory,
        ILogger<HangfireJobsService> logger,
        IHostEnvironment environment
    )
    {
        _recurringJobManager = recurringJobManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _isDevelopment = Debugger.IsAttached || environment.IsDevelopment();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering Hangfire recurring jobs...");

        _recurringJobManager.RemoveIfExists("update-symbols-history");

        // Список отслеживаемых пар — раз в день.
        _recurringJobManager.AddOrUpdate<SymbolUpdateWorker>(
            "update-symbols",
            worker => worker.ScanMarketAndUpdateSymbolsAsync(),
            Cron.Daily(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Тики → минутные свечи.
        _recurringJobManager.AddOrUpdate<OhlcvAggregatorWorker>(
            "ohlcv-aggregator",
            worker => worker.AggregateNextBatchAsync(),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Свечи → индикаторы.
        _recurringJobManager.AddOrUpdate<FeatureCalculatorWorker>(
            "feature-calculator",
            worker => worker.CalculateFeaturesAsync(),
            "*/2 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Дыры за последние 24 часа — то, что образуется при обрыве связи.
        _recurringJobManager.AddOrUpdate<QuickAuditorWorker>(
            "quick_audit",
            worker => worker.CheckAndFillRecentGapsAsync(),
            _isDevelopment ? "*/2 * * * *" : "*/10 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Вотермарки аудита для новых символов. Старт не раньше границы ретенции.
        _recurringJobManager.AddOrUpdate<AuditInitializationWorker>(
            "audit-initializer",
            worker => worker.CreateWatermarksForNewSymbolsAsync(),
            Cron.Daily(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Глубокая проверка истории на пропуски.
        _recurringJobManager.AddOrUpdate<HistoricalAuditorWorker>(
            "historical-audit",
            worker => worker.AuditNextBatchAsync(),
            _isDevelopment ? "*/30 * * * *" : "0 */6 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Ротация партиций по размеру диска.
        _recurringJobManager.AddOrUpdate<PartitionMaintenanceWorker>(
            "partition-maintenance",
            worker => worker.RotatePartitionsAsync(),
            Cron.Daily(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Проверки качества данных — только вручную, кнопкой на странице /DataQuality.
        _recurringJobManager.RemoveIfExists("data-quality-check");

        _logger.LogInformation("Recurring jobs registered.");

        using var scope = _scopeFactory.CreateScope();
        var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
        var activeSymbols = await symbolRepo.GetActiveSymbolsAsync();
        if (!activeSymbols.Any())
        {
            _logger.LogWarning("Symbol list is empty. Triggering unscheduled market scan...");
            _recurringJobManager.Trigger("update-symbols");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
