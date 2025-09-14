using BinanceDataCollector.Worker.Workers;
using Hangfire;

namespace BinanceDataCollector.Worker.Common;

public class HangfireJobsService : IHostedService
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ILogger<HangfireJobsService> _logger;

    public HangfireJobsService(IRecurringJobManager recurringJobManager, ILogger<HangfireJobsService> logger)
    {
        _recurringJobManager = recurringJobManager;
        _logger = logger;
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
            "0 22 * * *", // "0 22 * * *" => "в 00 минут, в 22 часа, каждый день, каждый месяц, каждый день недели"
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // "quick_audit": Каждые 5 минут. Проверять все символы на неполноту в рамках 24 часового окна
        _recurringJobManager.AddOrUpdate<QuickAuditorWorker>(
            "quick_audit",
            worker => worker.CheckAndFillRecentGapsAsync(),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // --------------------------------------------------------------------------------
        //_recurringJobManager.AddOrUpdate<HistoricalAuditorWorker>(
        //    "historical-audit",
        //    worker => worker.AuditNextBatchAsync(),
        //     "*/3 * * * *",
        //    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        //);

        //_recurringJobManager.AddOrUpdate<QuickAuditorWorker>(
        //    "quick_audit",
        //    worker => worker.CheckAndFillRecentGapsAsync(),
        //    "*/5 * * * *",
        //    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        //);
        // --------------------------------------------------------------------------------

        _recurringJobManager.AddOrUpdate<AuditInitializationWorker>(
            "audit-initializer",
            worker => worker.CreateWatermarksForNewSymbolsAsync(),
            Cron.Daily(), // Тоже раз в день
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        _logger.LogInformation("Периодические задачи зарегистрированы.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Ничего не делаем при остановке
        return Task.CompletedTask;
    }
}