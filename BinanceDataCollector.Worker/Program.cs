using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.MarketScreenService;
using BinanceDataCollector.Worker.Common;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using Hangfire.PostgreSql;
using Serilog;
using Serilog.Enrichers.WithCaller;

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        #region Минимальный "загрузочный" логгер - записать в консоль ошибку, если .Build() упадет.
        var configuration = new ConfigurationBuilder()
           .SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json")
           .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
           .Build();

        Log.Logger = new LoggerConfiguration()
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .CreateBootstrapLogger();
        #endregion

        try
        {
            Log.Information("Запускаем приложение...");
            var host = Host.CreateDefaultBuilder(args)
             .ConfigureServices((hostContext, services) => {
                 IConfiguration configuration = hostContext.Configuration;
                 
                 #region ===== НАСТРОЙКА HANGFIRE =====
                 services.AddHangfire(config => config
                     .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                     .UseSimpleAssemblyNameTypeSerializer()
                     .UseRecommendedSerializerSettings()
                     .UsePostgreSqlStorage(options => {
                         options.UseNpgsqlConnection(hostContext.Configuration.GetConnectionString("HangfireConnection"));
                     }
                 ));

                 services.AddHangfireServer(options =>
                 {
                     // Указываем, какие очереди и в каком порядке обрабатывать.
                     options.Queues = new[] {
                        "realtime",         // Высший: Мгновенные системные задачи
                        "live",             // Высокий: Сбор данных в реальном времени (если будете использовать)
                        "quick_audit",      // Средний: Быстрый аудит "горячих" данных
                        "historical_audit", // Низкий: Планирование "капитального ремонта"
                        "archive_import",   // Очень низкий: Скачивание и импорт тяжелых архивов
                        "default"           // Самый низкий: Все остальное, что без очереди
                     };
                     // WorkerCount можно настроить, но по умолчанию он = Environment.ProcessorCount * 5
                 });
                 #endregion

                 services.AddScoped<IBinanceService, BinanceService>();                 
                 services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();// сбор топ-Х пар по которым необходимо собирать статистику
                 services.AddScoped<IOrderRepository, OrderRepository>();   
                 services.AddScoped<ITradeRepository, TradeRepository>();
                 services.AddScoped<IAuditRepository, AuditRepository>();
                 services.AddTransient<IAuditService, AuditService>(); // <-- 
                 services.AddScoped<IHistoricalAuditRepository, HistoricalAuditRepository>(); // <-- 
                 services.AddScoped<IOhlcvRepository, OhlcvRepository>();       // расчёт аналитики - свечи
                 services.AddScoped<IFeatureRepository, FeatureRepository>();
                 services.AddScoped<IAnalysisRepository, AnalysisRepository>(); // расчёт 
                 services.AddTransient<IIndicatorService, IndicatorService>();  // расчёт аналитики - индикаторы
                 services.AddTransient<MarketScreener>();
                 services.AddTransient<SymbolUpdateWorker>();               // сервис обновления списка пар
                 services.AddTransient<HistoricalAuditorWorker>();          // Глубокое восстановление дыр
                 services.AddTransient<AuditInitializationWorker>();
                 services.AddTransient<QuickAuditorWorker>();           // Восстанавливаем дыры за 24 часа максимум
                 services.AddHostedService<HangfireJobsService>();
                 services.AddHostedService<DashboardHostedService>();
                 services.AddHostedService<BinanceCollectorWorker>();           // Собираем данные от binance и сохраняем в базу
             })
            .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.With<EnrichWithSourceClass>()
                .Enrich.WithCaller())
            .Build();

            Log.Information("Запуск хоста...");
            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Хост завершился с непредвиденной ошибкой");
            // TODO write to file
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}