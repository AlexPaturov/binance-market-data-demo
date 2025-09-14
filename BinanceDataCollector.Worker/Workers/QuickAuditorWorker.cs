using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Сервис, который проверяет целостность тиковых данных в таблице Trades и автоматически заполняет небольшие пробелы.
/// </summary>
public class QuickAuditorWorker 
{
    private readonly ILogger<QuickAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public QuickAuditorWorker(
        ILogger<QuickAuditorWorker> logger, 
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 720 * 60)] // 720 - 12 часов
    [Queue("quick_audit")]
    public async Task CheckAndFillRecentGapsAsync()
    {
        _logger.LogInformation("--- Начинаем быстрый аудит ---");
        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
                var activeSymbols = (await symbolRepo.GetActiveSymbolsAsync()).ToList();
                var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
                _logger.LogInformation("Обнаружено {Count} активных символов для проверки.", activeSymbols.Count);

                #region поиск дыр для каждой активной пары символов
                foreach (var symbol in activeSymbols)
                {
                    _logger.LogInformation("Проверяем символ: {Symbol}...", symbol); 
                   
                    var gaps = await tradeRepo.GetGapsForSymbolDayAsync(symbol);  // получить для каждой пары список дыр за 24 часа
                    foreach (var gap in gaps)
                    {
                        _logger.LogInformation("Начало дыры {StartId} окончание {EndId} для символа {Symbol}", gap.GapStart, gap.GapEnd, symbol);
                        await FillGapAsync(scope, symbol, gap);
                    }
                }
                #endregion
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка - QuickAuditorWorker");
                throw;
            }
        }
        _logger.LogInformation("--- Быстрый аудит закончен ---");
    }

    private async Task<bool> FillGapAsync(
        IServiceScope scope, 
        string symbol,
        DataGap gap
    )
    {
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        long currentFromId = gap.GapStart;
        long gapEndId = gap.GapEnd;
        long tradesToFetch = gapEndId - currentFromId + 1;

        _logger.LogWarning("[{Symbol}] Обнаружена дыра в {Count} сделок с ID {StartId} по {EndId}. Начинаем заполнение.", symbol, tradesToFetch, currentFromId, gapEndId);

        // Главный цикл, который работает, пока мы не "закроем" всю дыру
        while (currentFromId <= gapEndId)
        {
            long remainingTrades = gapEndId - currentFromId + 1;            // Динамически вычисляем, сколько еще осталось загрузить
            int currentLimit = (int)Math.Min(remainingTrades, 1000);        // Определяем размер следующего запроса: либо остаток, либо максимальная страница 1000
            var fetchResult = await binanceService.GetHistoricalAggTradesById(symbol, currentFromId, currentLimit);

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
                    _logger.LogInformation("[{Symbol}] [БЫСТРЫЙ АУДИТ] Успешно заполнено {Count} сделок. Последний ID: {LastId}.", symbol, fetchResult.Data.Count, lastFilledTrade.TradeId);

                    currentFromId = lastFilledTrade.TradeId + 1; // Сдвигаем курсор
                    
                    if (currentFromId > gapEndId) break; // Если мы заполнили все, что было в плане, выходим

                    await Task.Delay(500); // Вежливая пауза
                    break;

                case FetchStatus.ApiLimit:
                    _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Засыпаем на 5 минут...", symbol);
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    break;          // Остаемся в цикле, чтобы повторить запрос

                case FetchStatus.GeneralError:
                    _logger.LogError("[{Symbol}] Ошибка API при заполнении дыры. Прекращаем попытки для этого блока.", symbol);
                    return false;   // Ошибка, дыра не заполнена
            }
        }

        _logger.LogWarning("[{Symbol}] Дыра с ID {StartId} по {EndId} успешно заполнена.", symbol, gap.GapStart, gap.GapEnd);
        return true;
    }
}
