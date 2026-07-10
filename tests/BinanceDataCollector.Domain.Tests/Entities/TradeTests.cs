using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Domain.Tests.Entities;

public class TradeTests
{
    [Fact]
    public void Trade_CanRepresentBinanceTradePayload()
    {
        var trade = new Trade
        {
            TradeId = 123,
            Symbol = "BTCUSDT",
            Price = 100_000.12m,
            Quantity = 0.5m,
            QuoteQuantity = 50_000.06m,
            TradeTime = 1_718_000_000_000,
            IsBuyerMaker = true,
            IsBestMatch = true
        };

        Assert.Equal(123, trade.TradeId);
        Assert.Equal("BTCUSDT", trade.Symbol);
        Assert.Equal(100_000.12m, trade.Price);
        Assert.Equal(0.5m, trade.Quantity);
        Assert.Equal(50_000.06m, trade.QuoteQuantity);
        Assert.True(trade.IsBuyerMaker);
        Assert.True(trade.IsBestMatch);
    }
}
