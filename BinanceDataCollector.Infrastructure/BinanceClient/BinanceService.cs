using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;

namespace BinanceDataCollector.Infrastructure.BinanceClient;

public class BinanceService : IBinanceService
{
    private readonly ILogger<BinanceService> _logger;
    private readonly BinanceSocketClient _socketClient;
    private readonly BinanceRestClient _restClient;

    public BinanceService(ILogger<BinanceService> logger, IConfiguration configuration)
    {
        _logger = logger;

        _restClient = new BinanceRestClient(options => {
            options.RateLimitingBehaviour = RateLimitingBehaviour.Wait; // Эта настройка применяется ко всем REST API клиента (Spot, Futures и т.д.)
            // Здесь также можно задать API ключи, если они понадобятся
            // options.ApiCredentials = new BinanceApiCredentials("key", "secret");
        });

        _socketClient = new BinanceSocketClient(options => {
            options.RateLimitingBehaviour = RateLimitingBehaviour.Wait;     // Для сокет-клиента эта опция тоже есть и влияет на запросы подписки
            options.ReconnectInterval = TimeSpan.FromSeconds(30);           // Настройка интервала переподключения
        });
    }

    public async Task SubscribeToTradesAsync(string symbol, Func<Trade, Task> onTradeReceived, CancellationToken cancellationToken)
    {
        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(
            symbol, 
            async data => 
            {
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

                await onTradeReceived(trade);
            },
            cancellationToken
         );

        if (!result.Success)
        {
            _logger.LogError("Failed to subscribe to trade stream: {Error}", result.Error?.Message);
            // Можно выбросить исключение, чтобы вызывающий код знал о провале
            throw new InvalidOperationException($"Failed to subscribe to {symbol}: {result.Error?.Message}");
        }
    }

    public async Task<IEnumerable<BinanceSymbol>> GetExchangeSymbolsAsync()
    {
        var result = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync();
        if (!result.Success)
        {
            _logger.LogError("Не удалось получить информацию о бирже: {Error}", result.Error?.Message);
            return Enumerable.Empty<BinanceSymbol>();
        }
        return result.Data.Symbols;
    }

    public async Task<IEnumerable<Binance24HPrice>> Get24hTickerStatisticsAsync()
    {
        var result = await _restClient.SpotApi.ExchangeData.GetTickersAsync();
        if (!result.Success)
        {
            _logger.LogError("Не удалось получить 24-часовую статистику: {Error}", result.Error?.Message);
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
                _logger.LogError("[{Symbol}] Ошибка при загрузке исторических свечей: {Error}", symbol, result.Error?.Message);
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

    public async Task<IEnumerable<IBinanceRecentTrade>> GetHistoricalAggTradesAsync(
        string symbol, 
        DateTime startTime, 
        DateTime endTime, 
        CancellationToken cancellationToken
    )
    {
        var allTrades = new List<IBinanceRecentTrade>();
        var currentStartTime = startTime;

        // Цикл для постраничной загрузки данных, чтобы обойти лимит в 1000 записей
        while (currentStartTime < endTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _restClient.SpotApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                symbol, startTime: currentStartTime, endTime: endTime, limit: 1000);

            // Если Binance вернул ошибку или пустой массив, выходим из цикла
            if (!result.Success || !result.Data.Any())
            {
                if (!result.Success)
                {
                    _logger.LogError("[{Symbol}] Ошибка при загрузке исторических сделок: {Error}", symbol, result.Error?.Message);
                }
                break;
            }

            allTrades.AddRange(result.Data.Cast<IBinanceRecentTrade>());
            currentStartTime = result.Data.Last().TradeTime.AddMilliseconds(1); // Сдвигаем начальную точку для следующего запроса
            await Task.Delay(250, cancellationToken); // Вежливая пауза в 250 мс, чтобы не превысить лимиты API
        }
        return allTrades;
    }

}







