using System.Diagnostics;
using System.Net;
using System.Reflection;
using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Analytics.MarketScreeners.Services;
using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Messaging;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Infrastructure.Services;
using BinanceDataCollector.Worker.Common;
using BinanceDataCollector.Worker.Workers;
using BinanceDataCollector.Worker.Workers.Archives;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Enrichers.WithCaller;

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var startupStopwatch = Stopwatch.StartNew();

        #region Минимальный "загрузочный" логгер - записать в консоль ошибку, если .Build() упадет.

        var bootstrapConfig = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true) // Просто добавляем его, если он есть
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfig)
            .CreateBootstrapLogger();

        #endregion

        Log.Information("Serilog настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        try
        {
            Log.Information("Запускаем приложение...");
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(builder.Environment.IsDevelopment() ? 7001 : 8080); });

            Log.Information("WebApplicationBuilder создан за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            #region Logging preferences
            builder.Logging.ClearProviders();
            builder.Host.UseSerilog((context, loggerConfiguration) => {
                loggerConfiguration
                    .ReadFrom.Configuration(context.Configuration)
                    .Enrich.FromLogContext()
                    .Enrich.WithProcessId()
                    .Enrich.WithThreadId()
                    .Enrich.With<EnrichWithSourceClass>()
                    .Enrich.WithCaller();
                if (context.HostingEnvironment.IsDevelopment())
                    loggerConfiguration.WriteTo.File(
                        path: $"logs/app-{DateTime.Now:yyyyMMdd_HHmmss}.log",
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ThreadId}] {SourceContext:l}[{MemberName}] {Message:lj}{NewLine}{Exception}",
                        retainedFileCountLimit: 14);
            });
            #endregion

            builder.Services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromSeconds(60);
            });

            builder.Services.Configure<ArchivesSettings>(builder.Configuration.GetSection("ArchivesSettings"));
            builder.Services.Configure<RetentionSettings>(builder.Configuration.GetSection("RetentionSettings"));

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

            builder.Services.AddSingleton<IPathProvider, PathProvider>();
            
            // TODO: [Refactor] переделать на polly в наследниках IStatusNotifier
            // См. Issue #1
            builder.Services.AddSingleton<IStatusNotifier>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                // Асинхронно создаем и ждем, пока экземпляр будет готов.
                // .GetAwaiter().GetResult() - это способ синхронно дождаться async метода в синхронном коде.
                return RabbitMqStatusNotifier.CreateAsync(configuration).GetAwaiter().GetResult();
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
            builder.Services.AddScoped<MarketScreener>();
            builder.Services.AddScoped<SymbolUpdateWorker>();
            builder.Services.AddTransient<HistoricalAuditorWorker>();
            builder.Services.AddTransient<AuditInitializationWorker>();
            builder.Services.AddTransient<QuickAuditorWorker>();
            builder.Services.AddTransient<FillGapWorker>();
            builder.Services.AddTransient<IArchiveService, ArchiveService>();
            builder.Services.AddTransient<OnlineArchiveImportWorker>();
            builder.Services.AddTransient<FeatureCalculatorWorker>();
            builder.Services.AddTransient<PartitionMaintenanceWorker>();
            builder.Services.AddSingleton<GapProcessingTracker>();

            // Импорт архивов работает на остатке ресурса: перед каждой пачкой вставки
            // проверяет лаг свечи и ждёт, если реалтайм-конвейер не успевает.
            builder.Services.AddSingleton<IImportBackpressure, ImportBackpressure>();
            // builder.Services.AddTransient<ArchiveDownloaderWorker>(); // must be deleted
            builder.Services.AddTransient<IArchiveDownloaderWorker, ArchiveDownloaderWorker>();
            builder.Services.AddTransient<IArchiveUnpackerWorker, ArchiveUnpackerWorker>();
            builder.Services.AddTransient<IArchiveDeletionWorker, ArchiveDeletionWorker>();
            builder.Services.AddScoped<IDataQualityRepository, DataQualityRepository>();
            builder.Services.AddScoped<IOrderBookFeatureRepository, OrderBookFeatureRepository>();
            builder.Services.AddSingleton<IOrderBookFeatureCalculator, OrderBookFeatureCalculator>();
            builder.Services.AddTransient<DataQualityCheckWorker>();
            builder.Services.AddHostedService<HangfireJobsService>();

            // Конвейер обработки: постоянные потребители событий Postgres. Ждут NOTIFY
            // (миграция 010) и разбирают очередь кусками — каждый кусок отдельной
            // транзакцией. Пришли на смену расписанию `Cron.Minutely()`, у которого пачка
            // целиком не укладывалась в командный таймаут и откатывалась (13–14.07.2026).
            builder.Services.AddHostedService<OhlcvAggregationService>();
            builder.Services.AddHostedService<FeatureCalculationService>();

            // Realtime-сбор: подписка на WebSocket по активным парам. Это основной источник
            // данных. Импорт архивов — бутстрап истории и восстановление после долгого
            // простоя, а не рабочий режим.
            builder.Services.AddHostedService<BinanceCollectorWorker>();

            // Фичи стакана. Сырой L2 не хранится — из книги в памяти считаются готовые
            // числа и пишутся раз в минуту (~0.4 ГБ/месяц против ~190 ГБ у сырой глубины).
            builder.Services.AddHostedService<OrderBookCollectorWorker>();
            
            builder.Services.AddHealthChecks()
                .AddNpgSql(
                    builder.Configuration.GetConnectionString("DefaultConnection")!,
                    name: "pgbouncer",
                    timeout: TimeSpan.FromSeconds(5)); 
            #endregion

            Log.Information("Все сервисы зарегистрированы за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            #region Hangfire preferences
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage( options => options.UseNpgsqlConnection(
                        builder.Configuration.GetConnectionString("HangfireConnection")),
                    new PostgreSqlStorageOptions
                    {
                        QueuePollInterval = TimeSpan.FromSeconds(15),
                        InvisibilityTimeout = TimeSpan.FromHours(4),
                        UseNativeDatabaseTransactions = true,
                        // Must be false: PgBouncer transaction mode drops the session advisory locks
                        // Hangfire uses to guard concurrent schema creation → duplicate key crash on
                        // startup. The hangfire schema is created once, out of band.
                        PrepareSchemaIfNecessary = false,
                        SchemaName = "hangfire",
                        JobExpirationCheckInterval = TimeSpan.FromHours(1),
                        CountersAggregateInterval = TimeSpan.FromMinutes(5),
                        TransactionSynchronisationTimeout = TimeSpan.FromMinutes(5)
                    }));

            // --- Сервер для быстрых, приоритетных задач ---
            builder.Services.AddHangfireServer(options =>
            {
                options.ServerName = "PriorityServer";
                options.Queues = new[] { "realtime", "quick_audit" }; // Слушает ТОЛЬКО эти очереди
                options.WorkerCount =
                    (Debugger.IsAttached || builder.Environment.IsDevelopment())
                        ? Math.Max(4, Environment.ProcessorCount) // на деве 8 ядер у маширы
                        : Math.Max(2, Environment.ProcessorCount); // на проде 4 ядра
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

            // Дамп конфигурации содержит пароли и строки подключения. В Production логи
            // уходят в Seq — печатаем только локально, под отладчиком или в Development.
            if (Debugger.IsAttached || builder.Environment.IsDevelopment())
            {
                PrintConfiguration(builder.Configuration);
            }

            var app = builder.Build();
            
            //-- log begin

            Log.Information(
                "SERVICE STARTED {@Service}",
                new
                {
                    ServiceName = builder.Environment.ApplicationName.Substring(builder.Environment.ApplicationName.LastIndexOf('.') + 1),
                    ServiceRole = builder.Configuration["SERVICE_ROLE"] ?? "unknown",
                    Environment = builder.Environment.EnvironmentName,
                    Version = builder.Configuration["APP_VERSION"] ?? "unknown",
                    MachineName = Environment.MachineName,
                    ProcessId = Environment.ProcessId,
                    StartedAtUtc = DateTime.UtcNow,

                    ListeningUrls = builder.Configuration["ASPNETCORE_URLS"],
                    BehindReverseProxy = true,
                    TlsTermination = "Traefik/Cloudflare",

                    HealthLiveEndpoint = "/health/live",
                    HealthReadyEndpoint = "/health/ready",
                    HealthReadyDependsOn = new[] { "PostgreSQL" },
                    HealthIgnoredDependencies = new[] { "RabbitMQ" }
                }
            );
            //-- log end 
            
            Log.Information("Приложение собрано (Build) за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

            // --- 5. Настройка веб-пайплайна (Middleware) ---
            // Включаем Hangfire Dashboard
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = new[] { new HangfireAuthorizationFilter() }
            });

            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false
            });

            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = _ => true
            });
            
            Log.Information(
                "SERVICE READY {@Ready}",
                new
                {
                    ServiceName = builder.Environment.ApplicationName.Substring(builder.Environment.ApplicationName.LastIndexOf('.') + 1),
                    ReadyAtUtc = DateTime.UtcNow,
                    ReadyDependsOn = new[] { "PostgreSQL" }
                }
            );
            
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
            // TODO write to a file
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Полный дамп конфигурации — включая строки подключения, пароли Postgres и RabbitMQ.
    /// Вызывать ТОЛЬКО в Development или под отладчиком: в Production логи уезжают в Seq,
    /// и секреты оказались бы в общем хранилище логов.
    /// </summary>
    private static void PrintConfiguration(IConfiguration configuration)
    {
        Console.WriteLine("--- Configuration Debug View ---");
        foreach (var a in configuration.AsEnumerable())
        {
            Console.WriteLine($"{a.Key} = {a.Value}");
        }

        Console.WriteLine("--- End Configuration Debug View ---");
    }

    // TODO не забыть использовать на стадии добавления версии
    static string GetFormattedAppVersion()
    {
        var infoVer = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrEmpty(infoVer)) return "v0.0.0 (unknown)";

        // Стандартный формат в .NET 8+: "1.0.0+79428a83425..."
        var plusIndex = infoVer.IndexOf('+');

        if (plusIndex >= 0)
        {
            // Берем часть ДО плюса (версию)
            var version = infoVer[..plusIndex]; 
        
            // Берем часть ПОСЛЕ плюса (хеш)
            var fullHash = infoVer[(plusIndex + 1)..];
        
            // Отрезаем 7 символов от хеша
            var shortHash = fullHash.Length >= 7 ? fullHash[..7] : fullHash;

            return $"v{version} ({shortHash})";
        }

        // Если плюса нет (например, локально без git), просто возвращаем версию с 'v'
        return $"v{infoVer}";
    }
}