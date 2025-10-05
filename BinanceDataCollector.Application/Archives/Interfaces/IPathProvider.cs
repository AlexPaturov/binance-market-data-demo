namespace BinanceDataCollector.Application.Archives.Interfaces;

public interface IPathProvider
{
    string GetTradeArchivesPath();
    string GetTradeUnpackedPath();
    
    string GetOhlcvArchivesPath();
    string GetOhlcvUnpackedPath();
}