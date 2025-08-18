using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// для сохранения индикаторов
/// </summary>
public interface IFeatureRepository
{
    Task UpsertFeaturesAsync(IEnumerable<FeatureData> features);

    Task<long?> GetLastFeatureTimeAsync(string symbol);
}
