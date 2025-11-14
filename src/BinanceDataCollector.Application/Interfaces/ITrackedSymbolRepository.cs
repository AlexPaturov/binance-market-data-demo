namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Для отслеживания активности топ-X пар
/// </summary>
public interface ITrackedSymbolRepository
{
    Task<IEnumerable<string>> GetActiveSymbolsAsync();
    Task UpdateSymbolListAsync(IEnumerable<string> latestTopSymbols);
}
