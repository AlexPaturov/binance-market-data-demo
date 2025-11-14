using BinanceDataCollector.Application.Analytics.MarketScreeners.Models;

namespace BinanceDataCollector.Application.Analytics.MarketScreeners;

public interface IDataExporter
{
    void PrintToConsole(IEnumerable<InterestingPair> pairs);
    Task SaveToCsvAsync(IEnumerable<InterestingPair> pairs, string filePath);
}