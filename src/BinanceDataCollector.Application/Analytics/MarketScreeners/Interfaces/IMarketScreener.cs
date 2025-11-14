using BinanceDataCollector.Application.Analytics.MarketScreeners.Models;

namespace BinanceDataCollector.Application.Analytics.MarketScreeners;

public interface IMarketScreener
{
    Task<List<InterestingPair>> FindTopPairsAsync(
        int topN = 40,
        decimal minQuoteVolumeInMillion = 10m);
}