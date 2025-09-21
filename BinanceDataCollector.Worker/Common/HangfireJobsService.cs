using BinanceDataCollector.Worker.Workers;
using Hangfire;
using System.Diagnostics;

namespace BinanceDataCollector.Worker.Common;

public class HangfireJobsService : IHostedService
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ILogger<HangfireJobsService> _logger;
    private readonly bool _isDevelopment;
    //IHostEnvironment _environment;

    public HangfireJobsService(
        IRecurringJobManager recurringJobManager, 
        ILogger<HangfireJobsService> logger,
        IHostEnvironment environment
    )
    {
        _recurringJobManager = recurringJobManager;
        _logger = logger;
        _isDevelopment = Debugger.IsAttached || environment.IsDevelopment();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Регистрируем периодические задачи Hangfire...");

        _recurringJobManager.RemoveIfExists("update-symbols-history");

        // Вместо статического RecurringJob используем IRecurringJobManager из DI
        _recurringJobManager.AddOrUpdate<SymbolUpdateWorker>(
            "update-symbols",
            worker => worker.ScanMarketAndUpdateSymbolsAsync(),
            Cron.Daily(), // Запускать раз в день
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // "historical-audit": Запускать ОДИН РАЗ В ДЕНЬ в 00:00 по киевскому времени (примерно 22:00 UTC)
        _recurringJobManager.AddOrUpdate<HistoricalAuditorWorker>(
            "historical-audit",
            worker => worker.AuditNextBatchAsync(),
            _isDevelopment ? "*/30 * * * *" : "0 */6 * * *",  // prod каждые 6 часов по UTC 6,12,18,00 : dev каждые 30 минут
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // "quick_audit": Каждые 10 минут. Проверять все символы на неполноту в рамках 24 часового окна
        _recurringJobManager.AddOrUpdate<QuickAuditorWorker>(
            "quick_audit",
            worker => worker.CheckAndFillRecentGapsAsync(),
             _isDevelopment ? "*/2 * * * *" : "*/10 * * * *", // Если dev - каждые 2 минуты, если prod - каждые 10 минут
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        _recurringJobManager.AddOrUpdate<AuditInitializationWorker>(
            "audit-initializer",
            worker => worker.CreateWatermarksForNewSymbolsAsync(),
            Cron.Daily(), // Тоже раз в день
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Агрегация свечей - каждую минуту
        //_recurringJobManager.AddOrUpdate<OhlcvAggregatorWorker>(
        //    "ohlcv-aggregator",
        //    worker => worker.AggregateNextBatchAsync(),
        //    Cron.Minutely() // Каждую минуту
        //);

        // Расчет индикаторов - каждые 2 минуты
        //_recurringJobManager.AddOrUpdate<FeatureCalculatorWorker>(
        //    "feature-calculator",
        //    worker => worker.CalculateFeaturesAsync(),
        //    "*/2 * * * *" // Каждые 2 минуты
        //);

        _logger.LogInformation("Периодические задачи зарегистрированы.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Ничего не делаем при остановке
        return Task.CompletedTask;
    }
}