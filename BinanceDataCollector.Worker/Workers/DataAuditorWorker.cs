using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который проверяет целостность тиковых данных в таблице Trades
/// и автоматически заполняет небольшие пробелы.
/// </summary>
public class DataAuditorWorker : BackgroundService
{
    private readonly ILogger<DataAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация аудитора
    //private readonly TimeSpan _auditInterval = TimeSpan.FromHours(2);
    private readonly TimeSpan _auditInterval = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _maxGapToAutoFill = TimeSpan.FromHours(24);
    private readonly TimeSpan _minGapToTriggerFill = TimeSpan.FromMinutes(5);

    public DataAuditorWorker(ILogger<DataAuditorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер-аудитор ТИКОВЫХ данных запущен.");
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("--- Начинаем плановую проверку целостности ТИКОВЫХ данных ---");
            using (var scope = _serviceProvider.CreateScope())
            {
                var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var activeSymbols = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                _logger.LogInformation("Обнаружено {Count} активных символов для проверки.", activeSymbols.Count);

                foreach (var symbol in activeSymbols)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        await ProcessSymbolGapsAsync(scope, symbol, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Symbol}] Непредвиденная ошибка при аудиторской проверке символа.", symbol);
                    }
                }
            }
            _logger.LogInformation("--- Проверка целостности завершена. Следующий запуск через {Interval}. ---", _auditInterval);
            await Task.Delay(_auditInterval, stoppingToken);
        }
    }

    private async Task ProcessSymbolGapsAsync(IServiceScope scope, string symbol, CancellationToken stoppingToken)
    {
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();

        var lastTradeTimeMs = await tradeRepo.GetLastTradeTimeAsync(symbol);
        if (!lastTradeTimeMs.HasValue)
        {
            _logger.LogWarning("[{Symbol}] Нет исторических данных. Проверка пропущена.", symbol);
            return;
        }

        var lastTradeTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTradeTimeMs.Value).DateTime;
        var timeGap = DateTime.UtcNow - lastTradeTime;

        if (timeGap <= _minGapToTriggerFill)
        {
            _logger.LogInformation("[{Symbol}] Проверка пройдена. Дыр в данных не обнаружено.", symbol);
            return;
        }

        var startTime = lastTradeTime.AddMilliseconds(1);
        var endTime = DateTime.UtcNow;

        if (timeGap > _maxGapToAutoFill)
        {
            _logger.LogCritical(
                "[{Symbol}] ОБНАРУЖЕНА ОГРОМНАЯ ДЫРА в данных размером {GapDays:F1} дней! Диапазон: с {StartTime} по {EndTime}. Автозаполнение невозможно. Требуется ручная загрузка архива.",
                symbol, timeGap.TotalDays, startTime.ToString("yyyy-MM-dd HH:mm"), endTime.ToString("yyyy-MM-dd HH:mm"));
        }
        else
        {
            _logger.LogWarning("[{Symbol}] Обнаружена дыра в данных размером {GapHours:F1} часов. Начинаем заполнение диапазона: {StartTime} - {EndTime}.",
                symbol, timeGap.TotalHours, startTime.ToString("yyyy-MM-dd HH:mm"), endTime.ToString("yyyy-MM-dd HH:mm"));

            IEnumerable<Trade> historicalTrades = await binanceService.GetHistoricalAggTradesAsync(symbol, startTime, endTime, stoppingToken);

            var tradesToSave = historicalTrades.ToList();
            //var tradesToSave = historicalTrades.Select(t => new Trade
            //{
            //    TradeId = t.OrderId,
            //    Symbol = symbol,
            //    Price = t.Price,
            //    Quantity = t.BaseQuantity,
            //    QuoteQuantity = t.Price * t.BaseQuantity,
            //    TradeTime = new DateTimeOffset(t.TradeTime).ToUnixTimeMilliseconds(),
            //    IsBuyerMaker = t.BuyerIsMaker,
            //    IsBestMatch = t.IsBestMatch,
            //}).ToList();

            if (historicalTrades.Any())
            {
                await tradeRepo.BulkInsertAsync(tradesToSave);

                var firstTradeTime = DateTimeOffset.FromUnixTimeMilliseconds(tradesToSave.First().TradeTime).DateTime;
                var lastTradeTimeFilled = DateTimeOffset.FromUnixTimeMilliseconds(tradesToSave.Last().TradeTime).DateTime;

                _logger.LogInformation(
                    "[{Symbol}] УСПЕХ: Дыра успешно заполнена. Загружено {Count} сделок. Фактически закрытый диапазон: с {Start} по {End}.",
                    symbol,
                    tradesToSave.Count,
                    firstTradeTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    lastTradeTimeFilled.ToString("yyyy-MM-dd HH:mm:ss")
                );
            }
            else
            {
                _logger.LogWarning("[{Symbol}] Запрошен диапазон для заполнения дыры, но Binance не вернул данных.", symbol);
            }
        }
    }

}
