using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// для работы со свечами
/// </summary>
public interface IOhlcvRepository
{
    Task<IEnumerable<Ohlcv>> GetKlinesWithWarmupAsync(string symbol, long startTime, int warmupPeriod);
}
