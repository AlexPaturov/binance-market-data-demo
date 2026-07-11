using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAnalysisRepository
{
    Task<IEnumerable<CvdResult>> GetCvdForOhlcvAsync(string symbol, DateTime startTime, DateTime endTime);

    Task<List<DataGap>> FindGapsInWindowAsync(string symbol, long startTradeId, long endTradeId);
}
