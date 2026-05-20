using Binance.Net.Clients;
using Binance.Net.Enums;

using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Infrastructure.BinanceClient;

/// <summary>
/// 
/// </summary>
public class BinanceService : IBinanceService
{
    private readonly ILogger<BinanceService> _logger;
    private readonly BinanceSocketClient _socketClient;
    private readonly BinanceRestClient _restClient;

    public BinanceService(
        ILogger<BinanceService> logger,
        IConfiguration configuration
    )
    {
        _logger = logger;

        _restClient = new BinanceRestClient(options => {
            options.RateLimitingBehaviour = RateLimitingBehaviour.Wait;
        });

        _socketClient = new BinanceSocketClient(options => {
            options.RateLimitingBehaviour = RateLimitingBehaviour.Wait;     // Для сокет-клиента эта опция тоже есть и влияет на запросы подписки
            options.ReconnectInterval = TimeSpan.FromSeconds(30);           // Настройка интервала переподключения
        });

    }

    public async Task SubscribeToMultipleTradesAsync(IEnumerable<string> symbols, Action<Trade> onTradeReceived, CancellationToken cancellationToken)
    {
        // Используем мультиплексную подписку из Binance.Net
        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
            symbols,
            data => {
                // Маппинг из модели Binance.Net в нашу доменную модель
                var tradeEvent = data.Data;
                var trade = new Trade
                {
                    TradeId = tradeEvent.Id,
                    Symbol = tradeEvent.Symbol,
                    Price = tradeEvent.Price,
                    Quantity = tradeEvent.Quantity,
                    // Pseudocode:
                    // 1. Check the BinanceStreamTrade class for a property representing the quote quantity.
                    // 2. If not present, look for an alternative property (e.g., tradeEvent.Quantity * tradeEvent.Price).
                    // 3. Replace QuoteQuantity assignment with the correct calculation or property.

                    // Replace this line:
                    // QuoteQuantity = tradeEvent.QuoteQuantity,

                    // With this:
                    QuoteQuantity = tradeEvent.Quantity * tradeEvent.Price,
                    TradeTime = new DateTimeOffset(tradeEvent.TradeTime).ToUnixTimeMilliseconds(),
                    IsBuyerMaker = tradeEvent.BuyerIsMaker, // Fixed property name
                    IsBestMatch = true                      // In WebSocket streams, this is always the best match
                };
                onTradeReceived(trade); // Вызываем синхронный Action
            },
            cancellationToken);

        if (!result.Success)
        {
            _logger.LogError("Failed to subscribe to trade streams: {Error}", result.Error?.Message);
            return;
        }

        // Ждем, пока не придет сигнал отмены
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public async Task<IEnumerable<BinanceSymbol>> GetExchangeSymbolsAsync(CancellationToken cancellationToken = default)
    {

        var result = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync();
        if (!result.Success)
        {
            _logger.LogError("Failed to get exchange information: {Error}", result.Error?.Message);
            return Enumerable.Empty<BinanceSymbol>();
        }
        return result.Data.Symbols;

    }

    public async Task<IEnumerable<Binance24HPrice>> Get24hTickerStatisticsAsync()
    {
        var result = await _restClient.SpotApi.ExchangeData.GetTickersAsync();
        if (!result.Success)
        {
            _logger.LogError("Failed to get 24-hour ticker statistics: {Error}", result.Error?.Message);
            return Enumerable.Empty<Binance24HPrice>();
        }
        return (IEnumerable<Binance24HPrice>)result.Data;
    }

    public async Task<IEnumerable<BinanceSpotKline>> GetHistoricalKlinesAsync(
       string symbol,
       KlineInterval interval,
       DateTime startTime,
       DateTime endTime,
       CancellationToken cancellationToken
    )
    {
        var allKlines = new List<IBinanceKline>();
        var currentStartTime = startTime;

        while (currentStartTime < endTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol, interval, currentStartTime, endTime, 1000);

            if (!result.Success)
            {
                _logger.LogError("[{Symbol}] Error downloading historical klines: {Error}", symbol, result.Error?.Message);
                break; // Выходим из цикла при ошибке
            }
            if (!result.Data.Any())
            {
                break; // Данных больше нет
            }

            allKlines.AddRange(result.Data);
            currentStartTime = result.Data.Last().CloseTime;

            // Небольшая пауза, чтобы не превысить лимиты API
            await Task.Delay(250, cancellationToken);
        }
        return (IEnumerable<BinanceSpotKline>)allKlines;
    }

    // отказываемся от временных промежутков в пользу заполнений диапазонов по недостающим id
    public async Task<FetchResult> GetHistoricalAggTradesByTime(
        string symbol,
        DateTime startTime,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await _restClient.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(symbol, startTime: startTime, limit: 1000, ct: cancellationToken);

            if (!result.Success)
            {
                // --- ОБРАБОТКА ОШИБОК API ---
                if (result.Error != null)
                {
                    // Коды 429 (Too Many Requests) и 418 (IP Banned) - это ошибки лимитов
                    if (result.Error.Code == 429 || result.Error.Code == 418)
                    {
                        _logger.LogWarning("[{Symbol}] Binance API rate limit reached. Code: {Code}", symbol, result.Error.Code);
                        return FetchResult.ApiLimitResult();
                    }
                }

                _logger.LogError("[{Symbol}] Error downloading historical trades: {Error}", symbol, result.Error?.Message);
                return FetchResult.ErrorResult();
            }

            if (!result.Data.Any())
            {
                // Если данных нет, возвращаем успешный, но пустой результат
                return FetchResult.SuccessResult(new List<Trade>());
            }

            // --- Маппинг в доменную модель ---
            var trades = result.Data.Select(t => new Trade
            {
                TradeId = t.Id,
                Symbol = symbol,
                Price = t.Price,
                Quantity = t.Quantity,
                QuoteQuantity = t.Price * t.Quantity,
                TradeTime = new DateTimeOffset(t.TradeTime).ToUnixTimeMilliseconds(),
                IsBuyerMaker = t.BuyerIsMaker,
                IsBestMatch = t.WasBestPriceMatch,
            }).ToList();

            return FetchResult.SuccessResult(trades);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Unexpected error calling GetAggregatedTradeHistoryAsync.", symbol);
            return FetchResult.ErrorResult();
        }
    }

    public async Task<FetchResult> GetHistoricalAggTradesById(string symbol, long fromId, int limit = 1000)
    {
        try
        {
            // Используем перегрузку метода из Binance.Net с параметром fromId
            var result = await _restClient.SpotApi.ExchangeData.GetTradeHistoryAsync(symbol, fromId: fromId, limit: limit);

            if (!result.Success)
            {
                // --- ОБРАБОТКА ОШИБОК API ---
                if (result.Error != null)
                {
                    // Коды 429 (Too Many Requests) и 418 (IP Banned) - это ошибки лимитов
                    if (result.Error.Code == 429 || result.Error.Code == 418)
                    {
                        _logger.LogWarning("[{Symbol}] Binance API rate limit reached. Code: {Code}", symbol, result.Error.Code);
                        return FetchResult.ApiLimitResult();
                    }
                }

                _logger.LogError("[{Symbol}] Error downloading historical trades: {Error}", symbol, result.Error?.Message);
                return FetchResult.ErrorResult();
            }

            if (!result.Data.Any())
            {
                // Если данных нет, возвращаем успешный, но пустой результат
                return FetchResult.SuccessResult(new List<Trade>());
            }

            // --- Маппинг в нашу доменную модель ---
            var trades = result.Data.Select(t => new Trade
            {
                TradeId = t.OrderId,
                Symbol = symbol,
                Price = t.Price,
                Quantity = t.BaseQuantity,
                QuoteQuantity = t.Price * t.BaseQuantity,
                TradeTime = new DateTimeOffset(t.TradeTime).ToUnixTimeMilliseconds(),
                IsBuyerMaker = t.BuyerIsMaker,
                IsBestMatch = t.IsBestMatch,
            }).ToList();

            return FetchResult.SuccessResult(trades);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Unexpected error calling GetAggregatedTradeHistoryAsync with fromId {FromId}.", symbol, fromId);
            return FetchResult.ErrorResult();
        }
    }

    public async Task<FetchResult> GetHistoricalRawTradesAsync(
        string symbol,
        long fromId,
        CancellationToken cancellationToken,
        int limit = 1000
    )
    {
        try
        {
            // Используем другой метод из Binance.Net - GetTradeHistoryAsync, который обращается к эндпоинту /api/v3/trades (сырые сделки).
            var result = await _restClient.SpotApi.ExchangeData.GetTradeHistoryAsync(symbol, fromId: fromId, limit: limit, ct: cancellationToken);

            // --- Обработка ошибок API (аналогично старому методу) ---
            if (!result.Success)
            {
                if (result.Error != null)
                {
                    if (result.Error.Code == 429 || result.Error.Code == 418)
                    {
                        _logger.LogWarning("[{Symbol}] Binance API rate limit reached (GetTradeHistoryAsync). Code: {Code}", symbol, result.Error.Code);
                        return FetchResult.ApiLimitResult();
                    }
                }
                _logger.LogError("[{Symbol}] Error downloading raw historical trades: {Error}", symbol, result.Error?.Message);
                return FetchResult.ErrorResult();
            }

            if (!result.Data.Any())
            {
                return FetchResult.SuccessResult(new List<Trade>());
            }

            // --- Маппинг из BinanceTrade в нашу доменную модель Trade ---
            // Этот маппинг немного отличается от GetAggregatedTradeHistoryAsync
            var trades = result.Data.Select(t => new Trade
            {
                TradeId = t.OrderId,
                Symbol = symbol,
                Price = t.Price,
                Quantity = t.BaseQuantity
                ,
                QuoteQuantity = t.QuoteQuantity, // В сырых сделках это поле есть
                TradeTime = new DateTimeOffset(t.TradeTime).ToUnixTimeMilliseconds(),
                IsBuyerMaker = t.BuyerIsMaker,
                IsBestMatch = t.IsBestMatch,
                OrderId = t.OrderId // Также можем получить OrderId
            }).ToList();

            return FetchResult.SuccessResult(trades);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Unexpected error calling GetTradeHistoryAsync with fromId {FromId}.", symbol, fromId);
            return FetchResult.ErrorResult();
        }
    }

    public async Task<IEnumerable<Ohlcv>> GetHistoricalKlinesAsync(string symbol, DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
    {
        var allKlinesDomainModel = new List<Ohlcv>();
        var currentStartTime = startTime;
        _logger.LogInformation("[{Symbol}] Starting historical klines download from {Start} to {End}", symbol, startTime, endTime);

        // Цикл для пагинации, так как API отдает максимум 1000 свечей за раз
        while (currentStartTime < endTime && !cancellationToken.IsCancellationRequested)
        {
            // Этот запрос делает Hangfire, он в очереди quick_audit. 
            // Отдельный диспетчер здесь не нужен.

            // Вес запроса /klines - 1
            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol,
                KlineInterval.OneMinute,
                currentStartTime,
                endTime,
                1000, // Максимальный лимит
                ct: cancellationToken);

            if (!result.Success)
            {
                _logger.LogError("[{Symbol}] Error downloading Klines: {Error}", symbol, result.Error?.Message);
                // В реальной задаче Hangfire здесь лучше выбросить исключение, чтобы задача ушла на Retry
                // throw new Exception($"Failed to download klines for {symbol}: {result.Error?.Message}");
                break; // Для простоты пока прерываем цикл
            }

            if (!result.Data.Any())
            {
                break; // Данных в этом диапазоне больше нет, выходим из цикла
            }

            // --- КЛЮЧЕВОЙ МОМЕНТ: Маппинг из Binance.Net в нашу доменную модель ---
            var klinesPage = result.Data.Select(klineFromApi => new Ohlcv
            {
                Symbol = symbol,
                OpenTime = new DateTimeOffset(klineFromApi.OpenTime).ToUnixTimeMilliseconds(),
                OpenPrice = klineFromApi.OpenPrice,
                HighPrice = klineFromApi.HighPrice,
                LowPrice = klineFromApi.LowPrice,
                ClosePrice = klineFromApi.ClosePrice,
                Volume = klineFromApi.Volume
            });

            allKlinesDomainModel.AddRange(klinesPage);

            // Сдвигаем курсор для следующего запроса
            // Берем OpenTime последней свечи и прибавляем 1 минуту
            currentStartTime = result.Data.Last().OpenTime.AddMinutes(1);

            _logger.LogDebug("[{Symbol}] Downloaded {Count} klines. Next request from {NextStart}", symbol, result.Data.Count(), currentStartTime);

            // Вежливая пауза, чтобы не "долбить" API слишком часто
            await Task.Delay(500, cancellationToken);
        }

        _logger.LogInformation("[{Symbol}] Historical klines download complete. Total: {Count}", symbol, allKlinesDomainModel.Count);

        return allKlinesDomainModel;
    }

}







