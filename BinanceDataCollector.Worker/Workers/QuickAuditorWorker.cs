using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using static System.Formats.Asn1.AsnWriter;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который проверяет целостность тиковых данных в таблице Trades
/// и автоматически заполняет небольшие пробелы.
/// </summary>
public class QuickAuditorWorker : BackgroundService
{
    private readonly ILogger<QuickAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация аудитора
    private readonly TimeSpan _quickAuditInterval = TimeSpan.FromHours(1);                  // оставляем
    private readonly TimeSpan _apiLimitSleepDuration = TimeSpan.FromMinutes(5);             // оставляем
    private readonly TimeSpan _politeDelayBetweenRequests = TimeSpan.FromMilliseconds(500); // оставляем

    public QuickAuditorWorker(ILogger<QuickAuditorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер-аудитор ТИКОВЫХ данных запущен.");
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // переделать - я жду "какие-то 2 минуты", а может ждать не нужно? или ждать нужно больше?

        #region главный цикл программы
        while (!stoppingToken.IsCancellationRequested)
        {
            var startTime = DateTime.UtcNow; // логирую время выполнения запонения дыр для всех пар
            _logger.LogInformation("--- Начинаем плановую проверку целостности ТИКОВЫХ данных ---");

            #region создаём область, в которой будет выполнена работа
            using (var scope = _serviceProvider.CreateScope())
            {
                var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var activeSymbols = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
                _logger.LogInformation("Обнаружено {Count} активных символов для проверки.", activeSymbols.Count);

                #region поиск дыр для каждой активной пары символов
                foreach (var symbol in activeSymbols) 
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        // 1 получить для каждой пары список дыр за 24 часа
                        var gaps = await tradeRepo.GetGapsForSymbolDayAsync(symbol);
                        foreach (var gap in gaps) 
                        {
                            _logger.LogInformation("Начало дыры {StartId} окончание {EndId} для символа {Symbol}",
                                gap.GapStart, gap.GapEnd, symbol);
                            await FillGapAsync(scope, symbol, gap, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Операция быстрого аудита была отменена.");
                        break; // Выходим из foreach
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Symbol}] Непредвиденная ошибка при аудиторской проверке символа.", symbol);
                    }
                }
                #endregion

                if (stoppingToken.IsCancellationRequested)
                    break; // Если мы вышли из foreach по break, нужно выйти и из while.
            }
            #endregion

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("--- Проверка целостности завершена. Потраченное время {LostTime} Следующий запуск через {Interval}. ---", duration, _quickAuditInterval);
            await Task.Delay(_quickAuditInterval, stoppingToken); // ожидание до следующего запуска
        }
        #endregion
    }

    private async Task<bool> FillGapAsync(
        IServiceScope scope, 
        string symbol,
        DataGap gap, 
        CancellationToken stoppingToken)
    {
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        long currentFromId = gap.GapStart;
        long gapEndId = gap.GapEnd;
        long tradesToFetch = gapEndId - currentFromId + 1;

        _logger.LogWarning("[{Symbol}] Обнаружена дыра в {Count} сделок с ID {StartId} по {EndId}. Начинаем заполнение.",
            symbol, tradesToFetch, currentFromId, gapEndId);

        // Главный цикл, который работает, пока мы не "закроем" всю дыру
        while (currentFromId <= gapEndId && !stoppingToken.IsCancellationRequested)
        {
            // Динамически вычисляем, сколько еще осталось загрузить
            long remainingTrades = gapEndId - currentFromId + 1;
            // Определяем размер следующего запроса: либо остаток, либо максимальная страница 1000
            int currentLimit = (int)Math.Min(remainingTrades, 1000);

            var fetchResult = await binanceService.GetHistoricalAggTradesById(symbol, currentFromId, stoppingToken, currentLimit);

            switch (fetchResult.Status)
            {
                case FetchStatus.Success:
                    if (!fetchResult.Data.Any())
                    {
                        _logger.LogError("[{Symbol}] Binance не вернул данные для ID >= {FromId}, хотя должен был. Пропускаем дыру.", symbol, currentFromId);
                        return false; // Ошибка, дыра не заполнена
                    }

                    await tradeRepo.BulkInsertAsync(fetchResult.Data);

                    var lastFilledTrade = fetchResult.Data.Last();
                    _logger.LogInformation("[{Symbol}] [БЫСТРЫЙ АУДИТ] Успешно заполнено {Count} сделок. Последний ID: {LastId}.",
                        symbol, fetchResult.Data.Count, lastFilledTrade.TradeId);

                    currentFromId = lastFilledTrade.TradeId + 1; // Сдвигаем курсор

                    // Если мы заполнили все, что было в плане, выходим
                    if (currentFromId > gapEndId) break;

                    await Task.Delay(500, stoppingToken); // Вежливая пауза
                    break;

                case FetchStatus.ApiLimit:
                    _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Засыпаем на 5 минут...", symbol);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    break; // Остаемся в цикле, чтобы повторить запрос

                case FetchStatus.GeneralError:
                    _logger.LogError("[{Symbol}] Ошибка API при заполнении дыры. Прекращаем попытки для этого блока.", symbol);
                    return false; // Ошибка, дыра не заполнена
            }
        }

        // Проверяем, что вышли из цикла, потому что все заполнили, а не потому что отменили
        if (stoppingToken.IsCancellationRequested)
        {
            return false;
        }

        _logger.LogWarning("[{Symbol}] Дыра с ID {StartId} по {EndId} успешно заполнена.", symbol, gap.GapStart, gap.GapEnd);
        return true;
    }

}
