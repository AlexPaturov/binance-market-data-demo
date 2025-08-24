using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который проверяет целостность тиковых данных в таблице Trades
/// и автоматически заполняет небольшие пробелы.
/// </summary>
public class QuickDataAuditorWorker : BackgroundService
{
    private readonly ILogger<QuickDataAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация аудитора
    private readonly TimeSpan _auditInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _minGapToTriggerFill = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _apiLimitSleepDuration = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _politeDelayBetweenRequests = TimeSpan.FromMilliseconds(500);

    public QuickDataAuditorWorker(ILogger<QuickDataAuditorWorker> logger, IServiceProvider serviceProvider)
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

        // 1. Находим "дыру"
        var lastTradeTimeMs = await tradeRepo.GetLastTradeTimeAsync(symbol); // Полу
        if (!lastTradeTimeMs.HasValue)
        {
            _logger.LogWarning("[{Symbol}] Нет исторических данных, аудит невозможен. Требуется первоначальная загрузка.", symbol);
            return;
        }

        var lastTradeTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTradeTimeMs.Value).DateTime;
        if ((DateTime.UtcNow - lastTradeTime) <= _minGapToTriggerFill)
        {
            _logger.LogInformation("[{Symbol}] Проверка пройдена. Дыр в данных не обнаружено.", symbol);
            return;
        }

        _logger.LogWarning("[{Symbol}] Обнаружена дыра в данных, начиная с {LastTradeTime}. Начинаем процесс заполнения.",
            symbol, lastTradeTime.ToString("yyyy-MM-dd HH:mm:ss"));

        var currentStartTime = lastTradeTime.AddMilliseconds(1);

        // 2. "Умный" цикл заполнения
        while (!stoppingToken.IsCancellationRequested)
        {
            var fetchResult = await binanceService.GetHistoricalAggTradesAsync(symbol, currentStartTime, stoppingToken);

            switch (fetchResult.Status)
            {
                case FetchStatus.Success:
                    if (!fetchResult.Data.Any())
                    {
                        // Данные закончились, дыра заполнена
                        _logger.LogInformation("[{Symbol}] Дыра успешно заполнена. Новых сделок в этом диапазоне больше нет.", symbol);
                        return; // Выходим из цикла и метода
                    }

                    await tradeRepo.BulkInsertAsync(fetchResult.Data);

                    var firstTrade = fetchResult.Data.First();
                    var lastTrade = fetchResult.Data.Last();

                    _logger.LogInformation(
                        "[{Symbol}] [АУДИТ] Успешно заполнено {Count} сделок в диапазоне с {Start} по {End}.",
                        symbol, fetchResult.Data.Count,
                        DateTimeOffset.FromUnixTimeMilliseconds(firstTrade.TradeTime).ToString("HH:mm:ss"),
                        DateTimeOffset.FromUnixTimeMilliseconds(lastTrade.TradeTime).ToString("HH:mm:ss"));

                    // Сдвигаем курсор на следующую порцию
                    currentStartTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTrade.TradeTime).DateTime.AddMilliseconds(1);

                    // Вежливая пауза
                    await Task.Delay(_politeDelayBetweenRequests, stoppingToken);
                    break;

                case FetchStatus.ApiLimit:
                    _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит запросов к API. Засыпаем на {SleepDuration}...",
                        symbol, _apiLimitSleepDuration);
                    await Task.Delay(_apiLimitSleepDuration, stoppingToken);
                    // Остаемся в цикле, чтобы повторить тот же самый запрос
                    break;

                case FetchStatus.GeneralError:
                    _logger.LogError("[{Symbol}] Произошла ошибка API. Прекращаем попытки для этого символа в текущем цикле аудита.", symbol);
                    return; // Выходим из цикла и метода
            }
        }
    }

}
