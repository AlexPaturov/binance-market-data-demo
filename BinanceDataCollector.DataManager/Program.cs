using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Hangfire;
using Hangfire.PostgreSql;
using Serilog;
using System.Diagnostics;

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

        
        builder.Services.AddScoped<ITradeRepository, TradeRepository>();
        builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
        builder.Services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();
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

        // Включаем Hangfire Dashboard
        app.MapHangfireDashboard("/hangfire", new DashboardOptions {Authorization = new[] { new AllowAllConnectionsFilter() }});
        app.MapDefaultControllerRoute();

        Log.Information("Веб-пайплайн настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);

        Log.Information("Запуск хоста (app.Run)...");
        startupStopwatch.Stop();
        app.Run();
    }
}
