using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Tests.Analytics;

public class OrderBookFeatureCalculatorTests
{
    private readonly OrderBookFeatureCalculator _calculator = new();

    [Fact]
    public void Calculate_ComputesMidAndSpread()
    {
        var snapshot = _calculator.Calculate(
            bids: new[] { Level(99m, 1m) },
            asks: new[] { Level(101m, 1m) })!;

        Assert.Equal(100m, snapshot.MidPrice);
        Assert.Equal(99m, snapshot.BestBid);
        Assert.Equal(101m, snapshot.BestAsk);
        Assert.Equal(2m, snapshot.SpreadAbs);
        Assert.Equal(200m, snapshot.SpreadBps);   // 2 / 100 * 10000
    }

    [Fact]
    public void Calculate_Imbalance_IsPositiveWhenBuyersDominate()
    {
        // Покупателей втрое больше: (30 - 10) / 40 = 0.5
        var snapshot = _calculator.Calculate(
            bids: new[] { Level(99m, 30m) },
            asks: new[] { Level(101m, 10m) })!;

        Assert.Equal(0.5m, snapshot.Imbalance);
    }

    [Fact]
    public void Calculate_Imbalance_IsNegativeWhenSellersDominate()
    {
        var snapshot = _calculator.Calculate(
            bids: new[] { Level(99m, 10m) },
            asks: new[] { Level(101m, 30m) })!;

        Assert.Equal(-0.5m, snapshot.Imbalance);
    }

    [Fact]
    public void Calculate_Imbalance_IsZeroOnBalancedBook()
    {
        var snapshot = _calculator.Calculate(
            bids: new[] { Level(99m, 10m) },
            asks: new[] { Level(101m, 10m) })!;

        Assert.Equal(0m, snapshot.Imbalance);
    }

    [Fact]
    public void Calculate_Imbalance_UsesOnlyTopLevels()
    {
        // Гигантская заявка на 21-м уровне не должна влиять на дисбаланс:
        // считается ближняя часть книги, а не вся глубина.
        var bids = Enumerable.Range(0, OrderBookFeatureCalculator.ImbalanceLevels)
            .Select(i => Level(100m - i, 1m))
            .Append(Level(50m, 1_000_000m))
            .ToList();

        var asks = Enumerable.Range(0, OrderBookFeatureCalculator.ImbalanceLevels)
            .Select(i => Level(101m + i, 1m))
            .ToList();

        var snapshot = _calculator.Calculate(bids, asks)!;

        Assert.Equal(0m, snapshot.Imbalance);   // 20 против 20, «стенка» вдали не в счёт
    }

    [Fact]
    public void Calculate_Depth_CountsOnlyLevelsWithinDistanceFromMid()
    {
        // mid = 100. Порог 0.1% → границы 99.9 и 100.1.
        var bids = new[]
        {
            Level(99.95m, 1m),   // внутри 0.1%
            Level(99.92m, 2m),   // внутри 0.1%
            Level(99.50m, 4m),   // внутри 0.5%, но не 0.1%
            Level(98.00m, 8m)    // дальше 1%
        };
        var asks = new[]
        {
            Level(100.05m, 1m),
            Level(100.40m, 3m),  // внутри 0.5%
            Level(102.00m, 9m)   // дальше 1%
        };

        var snapshot = _calculator.Calculate(bids, asks)!;

        Assert.Equal(3m, snapshot.BidDepth01);   // 1 + 2
        Assert.Equal(1m, snapshot.AskDepth01);

        Assert.Equal(7m, snapshot.BidDepth05);   // 1 + 2 + 4
        Assert.Equal(4m, snapshot.AskDepth05);   // 1 + 3

        Assert.Equal(7m, snapshot.BidDepth10);   // 98.00 дальше 1% — не считается
        Assert.Equal(4m, snapshot.AskDepth10);
    }

    [Fact]
    public void Calculate_Walls_FindLargestOrderAndItsDistance()
    {
        // mid = 100. Крупнейшая заявка на покупку — на 99, это 100 bps ниже.
        var snapshot = _calculator.Calculate(
            bids: new[] { Level(99.9m, 1m), Level(99m, 50m), Level(98m, 2m) },
            asks: new[] { Level(100.1m, 1m), Level(101m, 80m) })!;

        Assert.Equal(50m, snapshot.MaxBidWall);
        Assert.Equal(100m, snapshot.MaxBidWallDistBps);   // (100 - 99) / 100 * 10000

        Assert.Equal(80m, snapshot.MaxAskWall);
        Assert.Equal(100m, snapshot.MaxAskWallDistBps);
    }

    [Fact]
    public void Calculate_OneSidedBook_ReturnsNull()
    {
        // Считать фичи по неполной книге хуже, чем не считать: они будут неверными.
        Assert.Null(_calculator.Calculate(new[] { Level(99m, 1m) }, Array.Empty<OrderBookLevel>()));
        Assert.Null(_calculator.Calculate(Array.Empty<OrderBookLevel>(), new[] { Level(101m, 1m) }));
    }

    [Fact]
    public void Aggregate_AveragesSamples_ButTakesMaximumOfWalls()
    {
        var samples = new[]
        {
            new OrderBookSnapshot { MidPrice = 100m, Imbalance = 0.2m, MaxBidWall = 10m, SpreadBps = 2m },
            new OrderBookSnapshot { MidPrice = 102m, Imbalance = 0.4m, MaxBidWall = 90m, SpreadBps = 4m },
        };

        var feature = _calculator.Aggregate("BTCUSDT", 1_767_225_600_000, samples, updateCount: 500);

        Assert.Equal(101m, feature.MidPrice);      // среднее
        Assert.Equal(0.3m, feature.Imbalance);     // среднее
        Assert.Equal(3m,   feature.SpreadBps);     // среднее

        // Стенку берём максимумом: важен сам факт, что заявка стояла, а не как долго.
        Assert.Equal(90m, feature.MaxBidWall);

        Assert.Equal(500, feature.UpdateCount);
        Assert.Equal(2,   feature.SampleCount);    // по нему видно, что были разрывы связи
    }

    [Fact]
    public void Aggregate_WithoutSamples_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _calculator.Aggregate("BTCUSDT", 0, Array.Empty<OrderBookSnapshot>(), 0));
    }

    private static OrderBookLevel Level(decimal price, decimal quantity) => new(price, quantity);
}
