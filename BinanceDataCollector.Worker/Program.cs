using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Infrastructure.Services;
using BinanceDataCollector.MarketScreenService;
using BinanceDataCollector.Worker.Common;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using Hangfire.PostgreSql;
using Serilog;
using Serilog.Enrichers.WithCaller;
using System.Diagnostics;
using System.Net;

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var startupStopwatch = Stopwatch.StartNew();

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
        Log.Information("Serilog настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        try
        {
            
            Log.Information("Запускаем приложение...");

            var builder = WebApplication.CreateBuilder(args);
            Log.Information("WebApplicationBuilder создан за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            builder.WebHost.UseUrls("http://*:7001");
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.With<EnrichWithSourceClass>()
                .Enrich.WithCaller()
                .CreateLogger());

            #region Настройка Hangfire
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options => {
                    options.UseNpgsqlConnection(
                        builder.Configuration.GetConnectionString("HangfireConnection"));
                }));

            // --- Сервер для быстрых, приоритетных задач ---
            builder.Services.AddHangfireServer(options =>
            {
                options.ServerName = "PriorityServer";
                options.Queues = new[] { "realtime", "quick_audit"}; // Слушает ТОЛЬКО эти очереди
               
                options.WorkerCount = 
                (Debugger.IsAttached || builder.Environment.IsDevelopment()) 
                    ? Math.Max(4, Environment.ProcessorCount)  // на деве 8 ядер у маширы
                    : Math.Max(2, Environment.ProcessorCount);  // на проде 4 ядра
            });

            // --- Сервер для тяжелых, фоновых задач ---
            builder.Services.AddHangfireServer(options =>
            {
                options.ServerName = "BackgroundServer";
                options.Queues = new[] { "historical_audit", "archive_import", "default" }; // Слушает ТОЛЬКО эти
                options.WorkerCount =
                (Debugger.IsAttached || builder.Environment.IsDevelopment()) 
                    ? Environment.ProcessorCount * 4 
                    : Environment.ProcessorCount * 2; // Выделяем ему ядра процессора
            });
            #endregion
            Log.Information("Hangfire запущен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            builder.Services.Configure<ArchivesSettings>(builder.Configuration.GetSection("ArchivesSettings"));
            #region Регистрация сервисов
            builder.Services.AddHttpClient("BinanceArchive", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10); // Большие архивы могут качаться долго
                client.DefaultRequestHeaders.Add("User-Agent", "BinanceDataCollector/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 10, // Ограничиваем подключения к Binance
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15), // Переиспользование соединений
                ConnectTimeout = TimeSpan.FromSeconds(30),
                ResponseDrainTimeout = TimeSpan.FromSeconds(10)
            });

            builder.Services.AddScoped<IBinanceService, BinanceService>();
            builder.Services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();
            builder.Services.AddScoped<ITradeRepository, TradeRepository>();
            builder.Services.AddScoped<IHistoricalAuditRepository, HistoricalAuditRepository>();
            builder.Services.AddScoped<IOhlcvRepository, OhlcvRepository>();
            builder.Services.AddScoped<IFeatureRepository, FeatureRepository>();
            builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
            builder.Services.AddScoped<IAuditRepository, AuditRepository>();
            builder.Services.AddTransient<IIndicatorService, IndicatorService>();
            builder.Services.AddTransient<MarketScreener>();
            builder.Services.AddTransient<SymbolUpdateWorker>();
            builder.Services.AddTransient<HistoricalAuditorWorker>();
            builder.Services.AddTransient<AuditInitializationWorker>();
            builder.Services.AddTransient<QuickAuditorWorker>();
            builder.Services.AddTransient<FillGapWorker>();
            builder.Services.AddTransient<IArchiveService, ArchiveService>();
            builder.Services.AddTransient<ArchiveImportWorker>();
            builder.Services.AddTransient<OhlcvAggregatorWorker>();
            builder.Services.AddTransient<FeatureCalculatorWorker>();
            builder.Services.AddSingleton<GapProcessingTracker>();

            // Ваши IHostedService
            builder.Services.AddHostedService<HangfireJobsService>();
            builder.Services.AddHostedService<BinanceCollectorWorker>();
            #endregion

            Log.Information("Все сервисы зарегистрированы за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            var app = builder.Build();
            Log.Information("Приложение собрано (Build) за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            
            // --- 5. Настройка веб-пайплайна (Middleware) ---
            // Включаем Hangfire Dashboard
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = new[] { new HangfireAuthorizationFilter() }
            });

            // Добавляем простой эндпоинт для проверки, что веб-сервер жив
            app.MapGet("/", () => "BinanceDataCollector is running.");
            Log.Information("Веб-пайплайн настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            Log.Information("Запуск хоста (app.Run)...");
            startupStopwatch.Stop();
            app.Run();
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