using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.MarketScreenService;
using BinanceDataCollector.Worker.Workers;

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) => 
        {
            IConfiguration configuration = hostContext.Configuration; // Получаем конфигурацию (appsettings.json)
            services.AddScoped<IDataSyncService, DataSyncService>();   // 1. Регистрация сервисов приложения
            services.AddScoped<IBinanceService, BinanceService>();      // 4. Регистрация внешних сервисов
            services.AddTransient<MarketScreener>(); // Сканер можно делать Transient
            services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();    // 2. Регистрация репозитория для сбора топ-Х пар по которым необходимо собирать статистику
            services.AddScoped<IOrderRepository, OrderRepository>();    // 3. Регистрация репозиториев (Dapper) Для каждого репозитория будет создаваться свой экземпляр
            services.AddScoped<ITradeRepository, TradeRepository>();
            services.AddHostedService<BinanceTradesWorker>();           // 5. Регистрация самого фонового воркера
            services.AddHostedService<SymbolUpdateWorker>(); //  Запускаем наш новый сервис обновления списка пар
            services.AddHostedService<DataAuditorWorker>(); // Восстанавливаем дыры за 24 часа максимум
            services.AddHostedService<OhlcvAggregatorWorker>(); // Агрегация тиковых данных в свечи

            // расчёт аналитики
            services.AddScoped<IOhlcvRepository, OhlcvRepository>();
            services.AddScoped<IFeatureRepository, FeatureRepository>();
            services.AddScoped<IAnalysisRepository, AnalysisRepository>();
            services.AddTransient<IndicatorService>(); // Transient, т.к. он stateless 
            services.AddHostedService<FeatureCalculatorWorker>();
        })
        .Build();

        host.Run();
    }
}