
using BinanceDataCollector.Application.Services;

namespace BinanceDataCollector.Worker.Workers;

public class BinanceTradesWorker : BackgroundService
{
    private readonly ILogger<BinanceTradesWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public BinanceTradesWorker(ILogger<BinanceTradesWorker> logger, 
        IServiceProvider serviceProvider, 
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Binance Trades Worker запущен в: {time}", DateTimeOffset.Now);

        try
        {
            // Создаем область видимости для получения scoped-сервисов
            using var scope = _serviceProvider.CreateScope();
            var dataSyncService = scope.ServiceProvider.GetRequiredService<DataSyncService>();

            var symbol = _configuration.GetValue<string>("Settings:Symbol") ?? "BTCUSDT";

            // Этот метод будет работать, пока не придет токен отмены
            await dataSyncService.StartTradeCollectionAsync(symbol);

        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Произошла критическая ошибка в воркере.");
        }
    }
}
