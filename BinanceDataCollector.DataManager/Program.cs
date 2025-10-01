using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common;
using BinanceDataCollector.DataManager.Hubs;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Serilog;
using System.Diagnostics;
using System.Net;

namespace BinanceDataCollector.DataManager;

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

        Log.Information("Запускаем приложение...");
        var builder = WebApplication.CreateBuilder(args);
        Log.Information("WebApplicationBuilder создан за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext());
        
        #region Настройка Hangfire
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => {
                options.UseNpgsqlConnection(
                    builder.Configuration.GetConnectionString("HangfireConnection"));
            }));
        builder.Services.AddHangfireServer();
        #endregion
        builder.Services.AddControllersWithViews();
        builder.Services.Configure<ArchivesSettings>(builder.Configuration.GetSection("ArchivesSettings"));
        builder.Services.AddHttpClient("BinanceArchive", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("User-Agent", "BinanceDataCollector/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10,                                                           // Ограничиваем подключения к Binance
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),                                    // Переиспользование соединений
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout = TimeSpan.FromSeconds(10)
        });

        builder.Services.AddScoped<ITradeRepository, TradeRepository>();
        builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
        builder.Services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();
        builder.Services.AddScoped<IArchiveService, ArchiveService>();
        Log.Information("Все сервисы зарегистрированы за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        var app = builder.Build();
        Log.Information("Приложение собрано (Build) за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseAuthorization();

        app.MapHangfireDashboard("/hangfire", new DashboardOptions {Authorization = new[] { new AllowAllConnectionsFilter() }});
        app.MapHub<ArchiveStatusHub>("/archiveStatusHub");
        app.MapDefaultControllerRoute();
        #region logging
        Log.Information("Веб-пайплайн настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
        startupStopwatch.Stop();
        Log.Information("Запуск хоста (app.Run)...");
        #endregion
        app.Run();
    }
}
