using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.MarketScreenService;
using BinanceDataCollector.Worker.Workers;
using Serilog; // <-- 1. Добавляем using

namespace BinanceDataCollector.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        // --- 2. Настраиваем "загрузочный" логгер Serilog ---
        // Это позволит логировать ошибки, которые происходят ДО создания хоста
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {

            var host = Host.CreateDefaultBuilder(args)
        // --- 3. Подключаем Serilog к системе логирования .NET ---
            .UseSerilog((context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration) // Читаем настройки из appsettings.json
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                // --- 4. Настраиваем отправку в Seq ---
                .WriteTo.Seq(serverUrl: context.Configuration["Seq:ServerUrl"])) // Берем URL из конфига
             .ConfigureServices((hostContext, services) => {
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
                    services.AddTransient<IIndicatorService, IndicatorService>();
                    services.AddHostedService<FeatureCalculatorWorker>();
                })
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
            // 6. Гарантируем, что все логи будут отправлены перед закрытием
            Log.CloseAndFlush();
        }
    }
}