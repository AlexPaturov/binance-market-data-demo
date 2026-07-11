using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BinanceDataCollector.Worker.Tests.Workers;

public class FeatureCalculatorWorkerTests
{
    private readonly Mock<IOhlcvRepository> _ohlcvRepo = new();
    private readonly Mock<IFeatureRepository> _featureRepo = new();
    private readonly Mock<IIndicatorService> _indicatorService = new();
    private readonly Mock<IAnalysisRepository> _analysisRepo = new();

    private FeatureCalculatorWorker CreateWorker() => new(
        NullLogger<FeatureCalculatorWorker>.Instance,
        _ohlcvRepo.Object,
        _featureRepo.Object,
        _indicatorService.Object,
        _analysisRepo.Object);

    [Fact]
    public async Task DoWorkAsync_WhenNewKlinesExist_UpsertsCalculatedFeaturesAndMarksProcessed()
    {
        const string symbol = "BTCUSDT";
        const long openTime = 1_700_000_000_000;

        var newKline = new Ohlcv { Symbol = symbol, OpenTime = openTime };
        var feature = new FeatureData { Symbol = symbol, OpenTime = openTime };

        _ohlcvRepo.Setup(r => r.ClaimNewKlinesForProcessingAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv> { newKline });
        _ohlcvRepo.Setup(r => r.GetWarmupKlinesAsync(symbol, It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv>());
        _indicatorService.Setup(s => s.CalculateAll(symbol, It.IsAny<IEnumerable<Ohlcv>>()))
            .Returns(new List<FeatureData> { feature });
        _analysisRepo.Setup(r => r.GetCvdForOhlcvAsync(symbol, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CvdResult>());

        await CreateWorker().DoWorkAsync(CancellationToken.None);

        _featureRepo.Verify(r => r.UpsertFeaturesAsync(
            It.Is<IEnumerable<FeatureData>>(features => features.Single().OpenTime == openTime)), Times.Once);
        _ohlcvRepo.Verify(r => r.MarkKlinesAsProcessedAsync(
            It.Is<IEnumerable<long>>(times => times.Single() == openTime)), Times.Once);
    }

    [Fact]
    public async Task DoWorkAsync_WhenNoNewKlines_DoesNotCalculateOrMark()
    {
        _ohlcvRepo.Setup(r => r.ClaimNewKlinesForProcessingAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv>());

        await CreateWorker().DoWorkAsync(CancellationToken.None);

        _indicatorService.Verify(
            s => s.CalculateAll(It.IsAny<string>(), It.IsAny<IEnumerable<Ohlcv>>()), Times.Never);
        _featureRepo.Verify(r => r.UpsertFeaturesAsync(It.IsAny<IEnumerable<FeatureData>>()), Times.Never);
        _ohlcvRepo.Verify(r => r.MarkKlinesAsProcessedAsync(It.IsAny<IEnumerable<long>>()), Times.Never);
    }
}
