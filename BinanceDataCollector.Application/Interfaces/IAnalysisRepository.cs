using BinanceDataCollector.Application.Analytics.Models;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAnalysisRepository
{
    Task<IEnumerable<CvdResult>> GetCvdForOhlcvAsync(string symbol, DateTime startTime, DateTime endTime);
}
