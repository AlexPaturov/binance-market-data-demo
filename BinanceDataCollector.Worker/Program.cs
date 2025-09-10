using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.MarketScreenService;
using BinanceDataCollector.Worker.Common;
using BinanceDataCollector.Worker.Workers;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Enrichers;
using Serilog.Enrichers.WithCaller;
using System.Threading.Channels;

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        // =================================================================================
        // ЭТАП 1: Минимальный "загрузочный" логгер.
        // Его единственная цель - записать в консоль ошибку, если .Build() упадет.
        // =================================================================================
        var configuration = new ConfigurationBuilder()
           .SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json")
           .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
           .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithShortSourceContext()
            .Enrich.WithThreadId()
            .Enrich.With<EnrichWithSourceClass>()
            .CreateLogger();

        try
        {
            Log.Information("Запускаем приложение...");
            var host = Host.CreateDefaultBuilder(args)
             .ConfigureServices((hostContext, services) => {
                 IConfiguration configuration = hostContext.Configuration;              // Получаем конфигурацию (appsettings.json)
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
                 services.AddSingleton<BinanceApiDispatcher>();

                 services.AddHostedService<SymbolUpdateWorker>();               // сервис обновления списка пар
                 services.AddHostedService<BinanceCollectorWorker>();           // Собираем данные от binance и сохраняем в базу
                 
                 // переделать с временных рядов на traidId
                 services.AddHostedService<QuickAuditorWorker>();           // Восстанавливаем дыры за 24 часа максимум

                 // переделать с временных рядов на traidId
                 //services.AddHostedService<HistoricalAuditorWorker>();          // Глубокое восстановление дыр
                 
                 
                 //services.AddHostedService<OhlcvAggregatorWorker>();          // Агрегация тиковых данных в свечи
                 //services.AddHostedService<FeatureCalculatorWorker>();
                 
             })
            .UseSerilog()
            .Build();

            Log.Information("Запуск хоста...");
            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Хост завершился с непредвиденной ошибкой");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}