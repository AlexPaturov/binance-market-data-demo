using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Analytics;

/// <summary>
/// Считает фичи стакана из одного его снимка. Чистая функция — ни БД, ни сети,
/// поэтому проверяется тестами напрямую.
/// </summary>
public class OrderBookFeatureCalculator : IOrderBookFeatureCalculator
{
    /// <summary>Глубина, по которой считается дисбаланс. 20 уровней — то, что показывает биржа.</summary>
    public const int ImbalanceLevels = 20;

    public OrderBookSnapshot? Calculate(
        IReadOnlyList<OrderBookLevel> bids,
        IReadOnlyList<OrderBookLevel> asks)
    {
        // Односторонняя книга — не книга. Считать по ней нечего.
        if (bids.Count == 0 || asks.Count == 0) return null;

        var bestBid = bids[0].Price;
        var bestAsk = asks[0].Price;
        if (bestBid <= 0 || bestAsk <= 0) return null;

        var mid = (bestBid + bestAsk) / 2m;
        var spread = bestAsk - bestBid;

        var topBidVolume = bids.Take(ImbalanceLevels).Sum(l => l.Quantity);
        var topAskVolume = asks.Take(ImbalanceLevels).Sum(l => l.Quantity);
        var totalTop = topBidVolume + topAskVolume;

        var (maxBidWall, maxBidWallPrice) = LargestOrder(bids);
        var (maxAskWall, maxAskWallPrice) = LargestOrder(asks);

        return new OrderBookSnapshot
        {
            MidPrice  = mid,
            BestBid   = bestBid,
            BestAsk   = bestAsk,
            SpreadAbs = spread,
            SpreadBps = Bps(spread, mid),

            // Перевес покупателей над продавцами в ближней части книги.
            Imbalance = totalTop == 0 ? 0m : (topBidVolume - topAskVolume) / totalTop,

            BidDepth01 = DepthWithin(bids, mid, 0.001m, below: true),
            AskDepth01 = DepthWithin(asks, mid, 0.001m, below: false),
            BidDepth05 = DepthWithin(bids, mid, 0.005m, below: true),
            AskDepth05 = DepthWithin(asks, mid, 0.005m, below: false),
            BidDepth10 = DepthWithin(bids, mid, 0.010m, below: true),
            AskDepth10 = DepthWithin(asks, mid, 0.010m, below: false),

            MaxBidWall        = maxBidWall,
            MaxBidWallDistBps = Bps(mid - maxBidWallPrice, mid),
            MaxAskWall        = maxAskWall,
            MaxAskWallDistBps = Bps(maxAskWallPrice - mid, mid)
        };
    }

    /// <summary>
    /// Усредняет снимки минуты. Одиночный снимок слишком шумный: книга меняется
    /// десятки раз в секунду, и значение «на секунде 59» — это случайность, а не свойство минуты.
    /// Стенки берём максимумом: важно, что заявка вообще стояла, а не как долго.
    /// </summary>
    public OrderBookFeature Aggregate(
        string symbol, long openTime, IReadOnlyList<OrderBookSnapshot> samples, int updateCount)
    {
        if (samples.Count == 0)
            throw new ArgumentException("Нет ни одного снимка стакана за минуту.", nameof(samples));

        return new OrderBookFeature
        {
            Symbol   = symbol,
            OpenTime = openTime,

            MidPrice  = samples.Average(s => s.MidPrice),
            BestBid   = samples.Average(s => s.BestBid),
            BestAsk   = samples.Average(s => s.BestAsk),
            SpreadAbs = samples.Average(s => s.SpreadAbs),
            SpreadBps = samples.Average(s => s.SpreadBps),
            Imbalance = samples.Average(s => s.Imbalance),

            BidDepth01 = samples.Average(s => s.BidDepth01),
            AskDepth01 = samples.Average(s => s.AskDepth01),
            BidDepth05 = samples.Average(s => s.BidDepth05),
            AskDepth05 = samples.Average(s => s.AskDepth05),
            BidDepth10 = samples.Average(s => s.BidDepth10),
            AskDepth10 = samples.Average(s => s.AskDepth10),

            MaxBidWall        = samples.Max(s => s.MaxBidWall),
            MaxBidWallDistBps = samples.Average(s => s.MaxBidWallDistBps),
            MaxAskWall        = samples.Max(s => s.MaxAskWall),
            MaxAskWallDistBps = samples.Average(s => s.MaxAskWallDistBps),

            UpdateCount = updateCount,
            SampleCount = samples.Count
        };
    }

    /// <summary>Объём заявок, стоящих не дальше <paramref name="fraction"/> от середины рынка.</summary>
    private static decimal DepthWithin(
        IReadOnlyList<OrderBookLevel> levels, decimal mid, decimal fraction, bool below)
    {
        var bound = below ? mid * (1m - fraction) : mid * (1m + fraction);

        decimal total = 0m;
        foreach (var level in levels)
        {
            // Уровни отсортированы от лучшей цены: как только вышли за границу — дальше не смотрим.
            if (below ? level.Price < bound : level.Price > bound) break;
            total += level.Quantity;
        }

        return total;
    }

    private static (decimal Quantity, decimal Price) LargestOrder(IReadOnlyList<OrderBookLevel> levels)
    {
        var max = levels[0];
        foreach (var level in levels)
        {
            if (level.Quantity > max.Quantity) max = level;
        }

        return (max.Quantity, max.Price);
    }

    private static decimal Bps(decimal delta, decimal mid) =>
        mid == 0 ? 0m : delta / mid * 10_000m;
}
