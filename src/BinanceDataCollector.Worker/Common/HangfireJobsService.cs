using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Регистрирует периодические задачи.
///
/// Рабочий режим: данные идут с WebSocket (`BinanceCollectorWorker`), аудиторы закрывают
/// дыры от обрывов связи, ротация следит за размером диска. Импорт архивов — бутстрап
/// истории и восстановление после долгого простоя, запускается вручную со страницы Archive.
///
/// Здесь только та работа, у которой поводом служит само время: обход рынка раз в сутки,
/// проверка на дыры, ротация партиций. Обработка данных поводом ко времени не привязана —
/// свечи и индикаторы считают постоянные потребители событий (`OhlcvAggregationService`,
/// `FeatureCalculationService`), которых будит появление работы, а не тик расписания.
/// </summary>
public class HangfireJobsService : IHostedService
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HangfireJobsService> _logger;
    private readonly bool _isDevelopment;
    private readonly bool _collectorsEnabled;

    public HangfireJobsService(
        IRecurringJobManager recurringJobManager,
        IServiceScopeFactory scopeFactory,
        ILogger<HangfireJobsService> logger,
        IHostEnvironment environment,
        IConfiguration configuration
    )
    {
        _recurringJobManager = recurringJobManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _isDevelopment = Debugger.IsAttached || environment.IsDevelopment();
        // Сбор из Binance можно выключить (demo-окружение без сети): тогда не регистрируются
        // джобы, обращающиеся к бирже. Данные при этом приходят из seed, а не из API.
        _collectorsEnabled = configuration.GetValue("Collectors:Enabled", true);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering Hangfire recurring jobs...");

        _recurringJobManager.RemoveIfExists("update-symbols-history");

        // Агрегация свечей и расчёт индикаторов больше не расписание, а постоянные
        // потребители событий Postgres (`OhlcvAggregationService`, `FeatureCalculationService`,
        // миграция 010). Снимаем расписания, оставшиеся на проде от таймерной модели:
        // без этого обе модели работали бы одновременно.
        _recurringJobManager.RemoveIfExists("ohlcv-aggregator");
        _recurringJobManager.RemoveIfExists("feature-calculator");

        // Джобы, обращающиеся к Binance API, — только когда сбор включён. В demo
        // (Collectors:Enabled=false) их нет: данные приходят из seed, а не из сети.
        if (_collectorsEnabled)
        {
            // Список отслеживаемых пар — раз в день.
            _recurringJobManager.AddOrUpdate<SymbolUpdateWorker>(
                "update-symbols",
                worker => worker.ScanMarketAndUpdateSymbolsAsync(),
                Cron.Daily(),
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
        }

        // Ротация партиций по размеру диска.
        _recurringJobManager.AddOrUpdate<PartitionMaintenanceWorker>(
            "partition-maintenance",
            worker => worker.RotatePartitionsAsync(),
            Cron.Daily(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Эвакуация закрытых месяцев на холодный диск здесь НЕ регистрируется: её планирует
        // сама БД через pg_cron (миграция 014, init/04_tablespace_and_cron.sql). Снимаем
        // расписание, оставшееся на проде от прежней Hangfire-версии.
        _recurringJobManager.RemoveIfExists("partition-evacuation");

        // Печати закрытых месяцев (MonthSeal) — точный критерий эвакуации. Решение требует
        // знания очереди импорта, поэтому его принимает приложение, а не pg_cron. Раз в час.
        _recurringJobManager.AddOrUpdate<PartitionMaintenanceWorker>(
            "month-seal-reconcile",
            worker => worker.ReconcileMonthSealsAsync(),
            Cron.Hourly(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc }
        );

        // Проверки качества данных — только вручную, кнопкой на странице /DataQuality.
        _recurringJobManager.RemoveIfExists("data-quality-check");

        _logger.LogInformation("Recurring jobs registered.");

        if (_collectorsEnabled)
        {
            using var scope = _scopeFactory.CreateScope();
            var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
            var activeSymbols = await symbolRepo.GetActiveSymbolsAsync();
            if (!activeSymbols.Any())
            {
                _logger.LogWarning("Symbol list is empty. Triggering unscheduled market scan...");
                _recurringJobManager.Trigger("update-symbols");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
