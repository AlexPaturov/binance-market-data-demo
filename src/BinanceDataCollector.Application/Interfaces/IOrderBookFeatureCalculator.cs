using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IOrderBookFeatureCalculator
{
    /// <summary>
    /// Считает фичи из одного снимка книги. Возвращает null, если книга односторонняя
    /// или цены нулевые — считать по такой нечего.
    /// </summary>
    OrderBookSnapshot? Calculate(IReadOnlyList<OrderBookLevel> bids, IReadOnlyList<OrderBookLevel> asks);

    /// <summary>Усредняет снимки минуты в одну запись.</summary>
    OrderBookFeature Aggregate(
        string symbol, long openTime, IReadOnlyList<OrderBookSnapshot> samples, int updateCount);
}

public interface IOrderBookFeatureRepository
{
    Task BulkUpsertAsync(IEnumerable<OrderBookFeature> features);

    Task<IEnumerable<OrderBookFeature>> GetAsync(string symbol, long fromMs, long toMs);
}
