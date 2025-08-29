using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IIndicatorService
{
    IEnumerable<FeatureData> CalculateAll(string symbol, IEnumerable<Ohlcv> klines);
}