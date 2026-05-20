using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using System.Diagnostics;

namespace BinanceDataCollector.Worker.Common;

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

        // Вместо статического RecurringJob используем IRecurringJobManager из DI
        _recurringJobManager.AddOrUpdate<SymbolUpdateWorker>(
            "update-symbols",
            worker => worker.ScanMarketAndUpdateSymbolsAsync(),
            Cron.Daily(), // Запускать раз в день
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // INITIAL LOAD: отключены до завершения фаз 3-5
        _recurringJobManager.RemoveIfExists("historical-audit");
        // _recurringJobManager.AddOrUpdate<HistoricalAuditorWorker>(
        //     "historical-audit",
        //     worker => worker.AuditNextBatchAsync(),
        //     _isDevelopment ? "*/30 * * * *" : "0 */6 * * *",
        //     new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        // );

        _recurringJobManager.RemoveIfExists("quick_audit");
        // _recurringJobManager.AddOrUpdate<QuickAuditorWorker>(
        //     "quick_audit",
        //     worker => worker.CheckAndFillRecentGapsAsync(),
        //     _isDevelopment ? "*/2 * * * *" : "*/10 * * * *",
        //     new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        // );

        _recurringJobManager.RemoveIfExists("audit-initializer");
        // _recurringJobManager.AddOrUpdate<AuditInitializationWorker>(
        //     "audit-initializer",
        //     worker => worker.CreateWatermarksForNewSymbolsAsync(),
        //     Cron.Daily(),
        //     new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        // );

        _recurringJobManager.RemoveIfExists("ohlcv-aggregator");
        // _recurringJobManager.AddOrUpdate<OhlcvAggregatorWorker>(
        //     "ohlcv-aggregator",
        //     worker => worker.AggregateNextBatchAsync(),
        //     Cron.Minutely()
        // );

        _recurringJobManager.RemoveIfExists("feature-calculator");
        // _recurringJobManager.AddOrUpdate<FeatureCalculatorWorker>(
        //     "feature-calculator",
        //     worker => worker.CalculateFeaturesAsync(),
        //     "*/2 * * * *"
        // );

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