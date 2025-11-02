namespace BinanceDataCollector.Application.Interfaces;

public interface ISymbolUpdateWorker
{
    Task ScanMarketAndUpdateSymbolsAsync();
}