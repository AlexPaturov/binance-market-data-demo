using System.Diagnostics;
using System.Net;
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
            builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.With<EnrichWithSourceClass>()
                .Enrich.WithCaller());
            #endregion

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
            builder.Services.AddTransient<OhlcvAggregatorWorker>();
            builder.Services.AddTransient<FeatureCalculatorWorker>();
            builder.Services.AddSingleton<GapProcessingTracker>();
            // builder.Services.AddTransient<ArchiveDownloaderWorker>(); // must be deleted
            builder.Services.AddTransient<IArchiveDownloaderWorker, ArchiveDownloaderWorker>();
            builder.Services.AddTransient<ArchiveUnpackerWorker>(); // 
            builder.Services.AddHostedService<HangfireJobsService>();
            //builder.Services.AddHostedService<BinanceCollectorWorker>();

            #region logs for deleting
            var rabbitUser = builder.Configuration["RabbitMQ:User"];
            var rabbitPass = builder.Configuration["RabbitMQ:Password"];
            var rabbitHost = builder.Configuration["RabbitMQ:Host"];
            var rabbitPort = builder.Configuration["RabbitMQ:Port"];

            Log.Information(
                "RabbitMQ config: User={User}, Host={Host}, Port={Port}, PassIsNull={PassIsNull}",
                rabbitUser,
                rabbitHost,
                rabbitPort,
                rabbitPass is null
            );
            
            var uriString = $"amqp://{Uri.EscapeDataString(rabbitUser)}:{Uri.EscapeDataString(rabbitPass)}@{rabbitHost}:{rabbitPort}/";
            Log.Information("amqp    " + uriString);
            #endregion
            
            builder.Services.AddHealthChecks()
                .AddNpgSql(
                    builder.Configuration.GetConnectionString("DefaultConnection")!,
                    name: "pgbouncer",
                    timeout: TimeSpan.FromSeconds(5))
                .AddRabbitMQ(
                    new Uri(
                        $"amqp://{builder.Configuration["RabbitMQ:UserName"]}:" +
                        $"{builder.Configuration["RabbitMQ:Password"]}@" +
                        $"{builder.Configuration["RabbitMQ:HostName"]}:" +
                        $"{builder.Configuration["RabbitMQ:Port"]}/"
                    ),
                    name: "rabbitmq",
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
                        PrepareSchemaIfNecessary = true,
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

            PrintConfiguration(builder.Configuration);
            var app = builder.Build();
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

    private static void PrintConfiguration(IConfiguration configuration)
    {
        Console.WriteLine("--- Configuration Debug View ---");
        foreach (var a in configuration.AsEnumerable())
        {
            Console.WriteLine($"{a.Key} = {a.Value}");
        }

        Console.WriteLine("--- End Configuration Debug View ---");
    }
}