
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Worker.Workers;

public class DeepDataAuditorWorker : BackgroundService
{
    private readonly ILogger<DeepDataAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация
    private readonly TimeSpan _auditInterval = TimeSpan.FromHours(4); // Проверяем реже, т.к. запрос тяжелый
    private readonly int _minGapInSeconds = 300; // Считаем дырой разрыв > 5 минут
    private readonly TimeSpan _apiLimitSleepDuration = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _politeDelayBetweenRequests = TimeSpan.FromMilliseconds(500);

    public DeepDataAuditorWorker(ILogger<DeepDataAuditorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер ГЛУБОКОГО АУДИТА данных запущен.");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Даем системе стабилизироваться

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("--- Начинаем плановую ГЛУБОКУЮ проверку целостности данных ---");
            using (var scope = _serviceProvider.CreateScope())
            {
                var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var activeSymbols = await symbolRepo.GetActiveSymbolsAsync();

                foreach (var symbol in activeSymbols)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        await ProcessSymbolDeepScanAsync(scope, symbol, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Если это отмена от пользователя, логируем как информацию и выходим
                        _logger.LogInformation("[{Symbol}] Глубокая проверка прервана сигналом отмены.", symbol);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Symbol}] Непредвиденная ошибка при глубокой проверке символа.", symbol);
                    }
                }
            }

            _logger.LogInformation("--- Глубокая проверка завершена. Следующий запуск через {Interval}. ---", _auditInterval);
            await Task.Delay(_auditInterval, stoppingToken);
        }
    }

    private async Task ProcessSymbolDeepScanAsync(IServiceScope scope, string symbol, CancellationToken stoppingToken)
    {
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();

        // 1. Находим ВСЕ дыры
        var gaps = (await analysisRepo.FindTradeGapsAsync(symbol, _minGapInSeconds)).ToList();

        if (!gaps.Any())
        {
            _logger.LogInformation("[{Symbol}] Проверка пройдена. Дыр в данных не обнаружено.", symbol);
            return;
        }

        _logger.LogWarning("[{Symbol}] Обнаружено {Count} дыр в данных. Начинаем процесс заполнения.", symbol, gaps.Count);

        // 2. Итерируемся по каждой дыре
        foreach (var gap in gaps)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var startTime = DateTimeOffset.FromUnixTimeMilliseconds(gap.GapStart).DateTime.AddMilliseconds(1);
            var endTime = DateTimeOffset.FromUnixTimeMilliseconds(gap.GapEnd).DateTime;

            _logger.LogInformation("[{Symbol}] Заполняем дыру с {StartTime} по {EndTime}", symbol, startTime, endTime);

            await FillGapAsync(symbol, startTime, endTime, binanceService, tradeRepo, stoppingToken);
        }
    }

    // "Умный" цикл-заполнитель для ОДНОЙ дыры
    private async Task FillGapAsync(string symbol, DateTime startTime, DateTime endTime, IBinanceService binanceService, ITradeRepository tradeRepo, CancellationToken stoppingToken)
    {
        var currentStartTime = startTime;
        while (currentStartTime < endTime && !stoppingToken.IsCancellationRequested)
        {
            var fetchResult = await binanceService.GetHistoricalAggTradesAsync(symbol, currentStartTime, stoppingToken);

            switch (fetchResult.Status)
            {
                case FetchStatus.Success:
                    if (!fetchResult.Data.Any())
                    {
                        _logger.LogInformation("[{Symbol}] Дыра успешно заполнена (Binance больше не возвращает данных).", symbol);
                        return; // Выходим из цикла
                    }

                    await tradeRepo.BulkInsertAsync(fetchResult.Data);

                    var lastTrade = fetchResult.Data.Last();
                    _logger.LogInformation("[{Symbol}] [АУДИТ] Загружено {Count} сделок до {LastTime}",
                        symbol, fetchResult.Data.Count, DateTimeOffset.FromUnixTimeMilliseconds(lastTrade.TradeTime));

                    currentStartTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTrade.TradeTime).DateTime.AddMilliseconds(1);
                    await Task.Delay(_politeDelayBetweenRequests, stoppingToken);
                    break;

                case FetchStatus.ApiLimit:
                    _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Засыпаем на {SleepDuration}...", symbol, _apiLimitSleepDuration);
                    await Task.Delay(_apiLimitSleepDuration, stoppingToken);
                    break;

                case FetchStatus.GeneralError:
                    _logger.LogError("[{Symbol}] Ошибка API. Прекращаем заполнение этой дыры.", symbol);
                    return; // Выходим из цикла
            }
        }
    }
}
