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
    public async Task ProcessNextBatchAsync_WhenNewKlinesExist_UpsertsCalculatedFeaturesAndMarksProcessed()
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

        var processed = await CreateWorker().ProcessNextBatchAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        _featureRepo.Verify(r => r.UpsertFeaturesAsync(
            It.Is<IEnumerable<FeatureData>>(features => features.Single().OpenTime == openTime)), Times.Once);
        // Свеча помечается по составному ключу: пометка только по OpenTime задевала бы
        // свечи других символов за ту же минуту.
        _ohlcvRepo.Verify(r => r.MarkKlinesAsProcessedAsync(
            It.Is<IEnumerable<Ohlcv>>(klines =>
                klines.Single().OpenTime == openTime && klines.Single().Symbol == symbol)), Times.Once);
    }

    /// <summary>
    /// Упавший символ не помечается выполненным: его свечи остаются 'processing' и
    /// возвращаются в расчёт перезахватом по протухшему ClaimedAt. Раньше пометка шла
    /// по всей пачке скопом — свечи упавшего символа получали конечный статус и
    /// оставались без фич навсегда (техдолг, п. 9 + 13).
    /// </summary>
    [Fact]
    public async Task ProcessNextBatchAsync_WhenOneSymbolFails_MarksOnlySucceededKlinesProcessed()
    {
        const long openTime = 1_700_000_000_000;
        var goodKline = new Ohlcv { Symbol = "BTCUSDT", OpenTime = openTime };
        var badKline = new Ohlcv { Symbol = "ETHUSDT", OpenTime = openTime };

        _ohlcvRepo.Setup(r => r.ClaimNewKlinesForProcessingAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv> { goodKline, badKline });
        _ohlcvRepo.Setup(r => r.GetWarmupKlinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv>());
        _indicatorService.Setup(s => s.CalculateAll(It.IsAny<string>(), It.IsAny<IEnumerable<Ohlcv>>()))
            .Returns((string s, IEnumerable<Ohlcv> _) => new List<FeatureData>
                { new() { Symbol = s, OpenTime = openTime } });
        _analysisRepo.Setup(r => r.GetCvdForOhlcvAsync("BTCUSDT", It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<CvdResult>());
        // Ровно так падал прод: CVD-запрос не укладывался в командный таймаут.
        _analysisRepo.Setup(r => r.GetCvdForOhlcvAsync("ETHUSDT", It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new TimeoutException("canceling statement due to user request"));

        var processed = await CreateWorker().ProcessNextBatchAsync(CancellationToken.None);

        // Взято в работу две свечи — для потребителя события очередь не пуста.
        Assert.Equal(2, processed);

        _ohlcvRepo.Verify(r => r.MarkKlinesAsProcessedAsync(
            It.Is<IEnumerable<Ohlcv>>(klines => klines.Single().Symbol == "BTCUSDT")), Times.Once);
    }

    /// <summary>
    /// Ноль — признак пустой очереди: по нему потребитель события понимает, что дошёл
    /// до дна и пора ждать следующего `NOTIFY`.
    /// </summary>
    [Fact]
    public async Task ProcessNextBatchAsync_WhenNoNewKlines_ReturnsZeroAndDoesNotCalculateOrMark()
    {
        _ohlcvRepo.Setup(r => r.ClaimNewKlinesForProcessingAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv>());

        var processed = await CreateWorker().ProcessNextBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);

        _indicatorService.Verify(
            s => s.CalculateAll(It.IsAny<string>(), It.IsAny<IEnumerable<Ohlcv>>()), Times.Never);
        _featureRepo.Verify(r => r.UpsertFeaturesAsync(It.IsAny<IEnumerable<FeatureData>>()), Times.Never);
        _ohlcvRepo.Verify(r => r.MarkKlinesAsProcessedAsync(It.IsAny<IEnumerable<Ohlcv>>()), Times.Never);
    }
}
