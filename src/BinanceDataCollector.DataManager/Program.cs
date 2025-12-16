using System.Diagnostics;
using System.Net;
using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common;
using BinanceDataCollector.DataManager.Hubs;
using BinanceDataCollector.DataManager.Messaging;
using BinanceDataCollector.DataManager.Middleware;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Infrastructure.Messaging;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Enrichers.WithCaller;

namespace BinanceDataCollector.DataManager;

public class Program
{
    public static void Main(string[] args)
    {
        var startupStopwatch = Stopwatch.StartNew();
        
        #region Минимальный "загрузочный" логгер - записать в консоль ошибку, если .Build() упадет.
        // --- ФАЗА 1: ЗАГРУЗОЧНЫЙ ЛОГГЕР ---
        // Создаем временную конфигурацию, чтобы Serilog знал, куда писать логи ДО старта хоста.
        var bootstrapConfig = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true) // Просто добавляем его, если он есть
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfig)
            .CreateBootstrapLogger();
        
        Log.Information("Serilog настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
        #endregion

        try {
            Log.Information("Start WebApplicationBuilder");
            
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(builder.Environment.IsDevelopment() ? 7002 : 8080); });
            
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
            #region Регистрация сервисов
            builder.Services.AddControllersWithViews();
            builder.Services.AddSignalR();
            builder.Services.Configure<ArchivesSettings>(builder.Configuration.GetSection("ArchivesSettings"));
            builder.Services.AddHttpClient("BinanceArchive", client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
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
            
            // TODO: [Refactor] переделать на polly -> RabbitMqStatusNotifier
            // См. Issue #1
            builder.Services.AddSingleton<IStatusNotifier>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return RabbitMqStatusNotifier.CreateAsync(configuration).GetAwaiter().GetResult(); 
            });
            builder.Services.AddScoped<ITradeRepository, TradeRepository>();
            builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
            builder.Services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();
            builder.Services.AddScoped<IArchiveService, ArchiveService>();
            builder.Services.AddHostedService<RabbitMQListenerService>();
            builder.Services.AddScoped<IDatabaseMonitoringService, DatabaseMonitoringService>();
            Log.Information("Все сервисы зарегистрированы за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            #endregion
            #region Настройка Hangfire
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options => {
                    options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("HangfireConnection")); 
                }));
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
            
            //PrintConfiguration(builder.Configuration); // TODO service information - delete
            var app = builder.Build();
            Log.Information("Приложение собрано (Build) за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            app.UseMiddleware<MemoryUsageLoggingMiddleware>();
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseAuthorization();

            app.MapHangfireDashboard("/hangfire", new DashboardOptions { Authorization = new[] { new AllowAllConnectionsFilter() } });
            app.MapHub<ArchiveStatusHub>("/archiveStatusHub");
            app.MapDefaultControllerRoute();

            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false
            });

            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = _ => true
            });
            
            #region logging
            Log.Information("Веб-пайплайн настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            startupStopwatch.Stop();
            Log.Information("Запуск хоста (app.Run)...");
            #endregion

            app.Run();
        }
        catch (Exception ex) {
            Log.Fatal(ex, "Хост завершился с непредвиденной ошибкой");
            // TODO write to a file
        }
        finally {
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