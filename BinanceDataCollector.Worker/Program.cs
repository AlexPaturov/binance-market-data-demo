using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Services;
using BinanceDataCollector.Infrastructure.BinanceClient;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Worker.Workers;

namespace BinanceDataCollector.Worker
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) => 
            {
                IConfiguration configuration = hostContext.Configuration; // Получаем конфигурацию (appsettings.json)
                services.AddScoped<DataSyncService>();                      // 1. Регистрация сервисов приложения
                services.AddScoped<IOrderRepository, OrderRepository>();    // 2. Регистрация репозиториев (Dapper) Для каждого репозитория будет создаваться свой экземпляр
                services.AddScoped<ITradeRepository, TradeRepository>();
                services.AddScoped<IBinanceService, BinanceService>();      // 3. Регистрация внешних сервисов
                services.AddScoped<IBinanceService, BinanceService>();  // 4. Добавление HttpClient для BinanceService (если он нужен)
                services.AddHostedService<BinanceTradesWorker>();           // 5. Регистрация самого фонового воркера
            })
            .Build();

            host.Run();
        }
    }
}