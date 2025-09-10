using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BinanceDataCollector.Worker.Workers;
/// <summary>
/// Выполняет глубокий, инкрементальный аудит исторических данных,
/// проверяя целостность последовательности TradeId небольшими окнами.
/// </summary>
public class HistoricalAuditorWorker : BackgroundService
{
    private readonly ILogger<HistoricalAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // --- Конфигурация ---
    private readonly TimeSpan _auditInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _retryInterval = TimeSpan.FromDays(1);
    private const int BatchSize = 5; // Сколько символов обрабатывать за один цикл
    private const int MaxRetries = 10;
    private readonly TimeSpan _windowSize = TimeSpan.FromDays(3);
    private readonly BinanceApiDispatcher _dispatcher;

    public HistoricalAuditorWorker(ILogger<HistoricalAuditorWorker> logger, IServiceProvider serviceProvider, BinanceApiDispatcher dispatcher   )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер исторического аудита (по TradeId) запущен.");
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // Первоначальная задержка

        while (!stoppingToken.IsCancellationRequested)
        {
            using (_logger.TimedOperation("Полный цикл исторического аудита"))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var auditRepo = scope.ServiceProvider.GetRequiredService<IHistoricalAuditRepository>();

                    // 1. Находим новые символы и создаем для них начальные вотермарки
                    await auditRepo.InitializeAuditForNewSymbolsAsync();

                    // 2. Получаем пачку "задач" на аудит
                    var symbolsToAudit = await auditRepo.GetSymbolsToAuditAsync(BatchSize, MaxRetries, _retryInterval);

                    if (!symbolsToAudit.Any())
                    {
                        _logger.LogInformation("Нет символов для исторического аудита в данный момент.");
                    }
                    else
                    {
                        foreach (var watermark in symbolsToAudit)
                        {
                            if (stoppingToken.IsCancellationRequested) break;
                            await ProcessSymbolAuditAsync(scope, watermark, stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Критическая ошибка в главном цикле исторического аудитора.");
                }
            }
            await Task.Delay(_auditInterval, stoppingToken);
        }
    }

    private async Task ProcessSymbolAuditAsync(IServiceScope scope, HistoricalWatermark watermark, CancellationToken stoppingToken)
    {
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IHistoricalAuditRepository>();
        var analysisService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        string symbol = watermark.Symbol;
        long startTradeId = watermark.LastChecked_TradeId + 1;

        using (_logger.TimedOperation("Аудит символа {Symbol} с TradeId {StartId}", symbol, startTradeId))
        {
            try
            {
                // 3. Определяем конец окна для проверки
                long endTimestamp = watermark.LastChecked_Timestamp + (long)_windowSize.TotalMilliseconds;
                long? endTradeId = await tradeRepo.GetLastTradeIdBeforeTimestampAsync(symbol, endTimestamp);

                // Случай 1: В 3-дневном окне нет новых сделок
                if (!endTradeId.HasValue || endTradeId.Value <= startTradeId)
                {
                    var lastTradeInDb = await tradeRepo.GetLastTradeAsync(symbol);
                    if (lastTradeInDb != null && lastTradeInDb.TradeTime > endTimestamp)
                    {
                        // Сделки есть, но они далеко в будущем. "Перепрыгиваем" пустое окно.
                        _logger.LogInformation("[{Symbol}] В окне до {EndTime} нет сделок. Перепрыгиваем...", symbol, DateTimeOffset.FromUnixTimeMilliseconds(endTimestamp));
                        await auditRepo.UpdateWatermarkAsync(symbol, lastTradeInDb.TradeId, lastTradeInDb.TradeTime, "Pending", false);
                    }
                    else
                    {
                        // Мы дошли до конца истории. Считаем аудит завершенным.
                        _logger.LogInformation("[{Symbol}] Достигнут конец истории. Аудит завершен.", symbol);
                        await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, "Completed", false);
                    }
                    return;
                }

                // 4. Ищем дыры в определенном окне
                var tradeIdsInWindow = await tradeRepo.GetTradeIdsInWindowAsync(symbol, startTradeId, endTradeId.Value);
                var gaps = analysisService.FindTradeIdGaps(tradeIdsInWindow).ToList();
                bool allGapsFilled = true;

                if (gaps.Any())
                {
                    _logger.LogWarning("[{Symbol}] В диапазоне ID {StartId}-{EndId} найдено {Count} дыр.", symbol, startTradeId, endTradeId.Value, gaps.Count);
                    foreach (var gap in gaps)
                    {
                        if (stoppingToken.IsCancellationRequested) { allGapsFilled = false; break; }
                        bool success = await FillGapAsync(scope, symbol, gap, stoppingToken);
                        if (!success) { allGapsFilled = false; break; }
                    }
                }
                else
                {
                    _logger.LogInformation("[{Symbol}] Дыр в диапазоне ID {StartId}-{EndId} не найдено.", symbol, startTradeId, endTradeId.Value);
                }

                // 5. Обновляем вотермарку
                if (allGapsFilled)
                {
                    await auditRepo.UpdateWatermarkAsync(symbol, endTradeId.Value, endTimestamp, "Pending", false);
                    _logger.LogInformation("[{Symbol}] Успешно проверен диапазон до TradeId {EndId}. Вотермарка сдвинута.", symbol, endTradeId.Value);
                }
                else
                {
                    var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                    await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
                    _logger.LogError("[{Symbol}] Не удалось заполнить дыры в диапазоне {StartId}-{EndId}. Попытка #{RetryCount}",
                        symbol, startTradeId, endTradeId.Value, watermark.RetryCount + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Symbol}] Критическая ошибка при аудите диапазона, начиная с TradeId {StartId}.", symbol, startTradeId);
                var newStatus = (watermark.RetryCount + 1 >= MaxRetries) ? "Failed_MaxRetries" : "Failed";
                await auditRepo.UpdateWatermarkAsync(symbol, watermark.LastChecked_TradeId, watermark.LastChecked_Timestamp, newStatus, true);
            }
        }
    }

    private async Task<bool> FillGapAsync(IServiceScope scope, string symbol, DataGap gap, CancellationToken stoppingToken)
    {
        var binanceService = scope.ServiceProvider.GetRequiredService<IBinanceService>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        long currentFromId = gap.GapStart + 1;
        long gapEndId = gap.GapEnd - 1;
        long tradesToFetch = gapEndId - currentFromId + 1;

        if (tradesToFetch <= 0) return true;

        using (_logger.TimedOperation(LogLevel.Warning, "[{Symbol}] Заполнение дыры в {Count} сделок с ID {StartId} по {EndId}", symbol, tradesToFetch, currentFromId, gapEndId))
        {
            // Перед КАЖДЫМ запросом в цикле мы запрашиваем доступ с самым низким приоритетом.
            using (await _dispatcher.AquireAccessAsync(ApiRequestPriority.HistoricalAudit, stoppingToken))
            {
                while (currentFromId <= gapEndId && !stoppingToken.IsCancellationRequested)
                {
                    long remainingTrades = gapEndId - currentFromId + 1;
                    int currentLimit = (int)Math.Min(remainingTrades, 1000);

                    // Используем новый метод для получения СЫРЫХ сделок
                    var fetchResult = await binanceService.GetHistoricalRawTradesAsync(symbol, currentFromId, stoppingToken, currentLimit);

                    switch (fetchResult.Status)
                    {
                        case FetchStatus.Success:
                            if (!fetchResult.Data.Any())
                            {
                                _logger.LogError("[{Symbol}] Binance не вернул данные для ID >= {FromId}, хотя должен был. Заполнение дыры прервано.", symbol, currentFromId);
                                return false;
                            }

                            await tradeRepo.BulkInsertAsync(fetchResult.Data);
                            var lastFilledTrade = fetchResult.Data.Last();
                            currentFromId = lastFilledTrade.TradeId + 1;

                            if (currentFromId > gapEndId) break;
                            await Task.Delay(500, stoppingToken);
                            break;

                        case FetchStatus.ApiLimit:
                            _logger.LogError("[{Symbol}] [API LIMIT] Превышен лимит. Засыпаем на 5 минут...", symbol);
                            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                            break;

                        case FetchStatus.GeneralError:
                            _logger.LogError("[{Symbol}] Ошибка API при заполнении дыры. Прекращаем попытки.", symbol);
                            return false;
                    }
                }
            }

            return !stoppingToken.IsCancellationRequested;
        }
    }
}