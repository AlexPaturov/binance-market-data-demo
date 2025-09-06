using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
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
            .Enrich.With<EnrichWithSourceClass>()
            .CreateLogger();

        try
        {
            Log.Information("Запускаем приложение...");
            var host = Host.CreateDefaultBuilder(args)
             .ConfigureServices((hostContext, services) => {
                 IConfiguration configuration = hostContext.Configuration;              // Получаем конфигурацию (appsettings.json)
                 services.AddScoped<IBinanceService, BinanceService>();                 // 4. Регистрация внешних сервисов
                 services.AddTransient<MarketScreener>();                               // Сканер можно делать Transient
                 services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();// 2. Регистрация репозитория для сбора топ-Х пар по которым необходимо собирать статистику
                 services.AddScoped<IOrderRepository, OrderRepository>();   // Регистрация репозиториев (Dapper) Для каждого репозитория будет создаваться свой экземпляр
                 services.AddScoped<ITradeRepository, TradeRepository>();
                 services.AddScoped<IAuditRepository, AuditRepository>();
                 services.AddHostedService<SymbolUpdateWorker>();               // сервис обновления списка пар
                 services.AddHostedService<BinanceCollectorWorker>();           // Собираем данные от binance и сохраняем в базу
                 services.AddHostedService<QuickDataAuditorWorker>();           // Восстанавливаем дыры за 24 часа максимум
                 services.AddHostedService<HistoricalAuditorWorker>();          // Агрегация тиковых данных в свечи
                 //services.AddHostedService<OhlcvAggregatorWorker>();            // Агрегация тиковых данных в свечи
                 //services.AddHostedService<FeatureCalculatorWorker>();

                 // расчёт аналитики
                 services.AddScoped<IOhlcvRepository, OhlcvRepository>();
                 services.AddScoped<IFeatureRepository, FeatureRepository>();
                 services.AddScoped<IAnalysisRepository, AnalysisRepository>();
                 services.AddTransient<IIndicatorService, IndicatorService>();
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