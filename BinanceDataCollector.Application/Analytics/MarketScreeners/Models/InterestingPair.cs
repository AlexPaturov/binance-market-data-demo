namespace BinanceDataCollector.Application.Analytics.MarketScreeners.Models;

public record InterestingPair(string Symbol, decimal QuoteVolume, decimal PriceChangePercent);