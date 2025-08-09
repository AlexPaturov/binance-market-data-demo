using Binance.Net.Clients;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Infrastructure.BinanceClient;

public class BinanceService : IBinanceService
{
    private readonly ILogger<BinanceService> _logger;
    private readonly BinanceSocketClient _socketClient;
    //private readonly HttpClient _httpClient;

    public BinanceService(ILogger<BinanceService> logger, 
        IConfiguration configuration
        //,HttpClient httpClient
        )
    {
        _logger = logger;
        _socketClient = new BinanceSocketClient(options => 
        { 
            //options.ApiCredentials = new Binance.Net.Objects.BinanceApiCredentials(
            //    configuration["Binance:ApiKey"], 
            //    configuration["Binance:ApiSecret"]);
            //options.LogVerbosity = Binance.Net.Enums.LogVerbosity.Info;
            //options.LogWriters.Add(Console.Out);
        });
        //_httpClient = httpClient;
    }

    public async Task SubscribeToTradesAsync(string symbol, Func<Trade, Task> onTradeReceived)
    {
        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(symbol, async data => {
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
                IsBestMatch = true,                     // In WebSocket streams, this is always the best match
            };

            await onTradeReceived(trade);
        });

        if (!result.Success)
        {
            _logger.LogError("Failed to subscribe to trade stream: {Error}", result.Error?.Message);
        }
    }
}
