using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;

namespace BinanceDataCollector.Worker.Tests.Workers;

public class FeatureCalculatorWorkerTests : IDisposable
{
    // --- Моки ---
    private readonly ILogger<FeatureCalculatorWorkerTests> _testLogger; // логгер для самого теста
    private readonly ILogger<FeatureCalculatorWorker> _workerLogger;    // логгер для передачи в качестве параметра
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<ITrackedSymbolRepository> _mockSymbolRepo;
    private readonly Mock<IOhlcvRepository> _mockOhlcvRepo;
    private readonly Mock<IFeatureRepository> _mockFeatureRepo;
    private readonly Mock<IAnalysisRepository> _mockAnalysisRepo;
    private readonly Mock<IIndicatorService> _mockIndicatorService;

    public FeatureCalculatorWorkerTests()
    {
        // --- Настройка Serilog ---
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug().WriteTo.Seq("http://localhost:5341").CreateLogger();
        var loggerFactory = new LoggerFactory().AddSerilog();
        _testLogger = loggerFactory.CreateLogger<FeatureCalculatorWorkerTests>();

        // ОТДЕЛЬНЫЙ логгер специально для передачи в конструктор воркера
        _workerLogger = loggerFactory.CreateLogger<FeatureCalculatorWorker>();

        // --- Подготовка моков ---
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockSymbolRepo = new Mock<ITrackedSymbolRepository>();
        _mockOhlcvRepo = new Mock<IOhlcvRepository>();
        _mockFeatureRepo = new Mock<IFeatureRepository>();
        _mockAnalysisRepo = new Mock<IAnalysisRepository>();
        _mockIndicatorService = new Mock<IIndicatorService>();

        // --- Настройка цепочки DI ---
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IServiceScopeFactory))).Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(factory => factory.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(scope => scope.ServiceProvider).Returns(_mockServiceProvider.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(ITrackedSymbolRepository))).Returns(_mockSymbolRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IOhlcvRepository))).Returns(_mockOhlcvRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IFeatureRepository))).Returns(_mockFeatureRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IAnalysisRepository))).Returns(_mockAnalysisRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IIndicatorService))).Returns(_mockIndicatorService.Object);
    }

    [Fact]
    public async Task DoWorkAsync_WhenKlinesExist_CallsUpsertFeatures()
    {
        // --- ARRANGE ---
        var symbol = "BTCUSDT";
        var klinesToReturn = new List<Ohlcv>
        {
            new Ohlcv
            {
                Symbol = symbol, // <-- Добавлено
                OpenTime = 1,
                OpenPrice = 1, // <-- Добавлено (предполагая, что они тоже required)
                HighPrice = 1, // <-- Добавлено
                LowPrice = 1,  // <-- Добавлено
                ClosePrice = 1, // <-- Добавлено
                Volume = 1,     // <-- Добавлено
            }
        };

        var featuresToReturn = new List<FeatureData> 
        {
            new FeatureData
            {
                Symbol = symbol, // <-- Добавлено
                OpenTime = 1
            }         
        };

        // Настраиваем моки на возврат простых данных
        _mockSymbolRepo.Setup(r => r.GetActiveSymbolsAsync()).ReturnsAsync(new List<string> { symbol });
        //_mockOhlcvRepo.Setup(r => r.GetAllBySymbolAsync(symbol)).ReturnsAsync(klinesToReturn);
        _mockIndicatorService.Setup(s => s.CalculateAll(symbol, klinesToReturn)).Returns(featuresToReturn);
        _mockFeatureRepo.Setup(r => r.GetLastFeatureTimeAsync(symbol)).ReturnsAsync((long?)null); // Для "оглупленной" версии это не важно
        _mockAnalysisRepo.Setup(r => r.GetCvdForOhlcvAsync(symbol, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(new List<CvdResult>());

        var worker = new FeatureCalculatorWorker(_workerLogger, _mockServiceProvider.Object);

        // --- ACT ---
        await worker.DoWorkAsync(CancellationToken.None);

        // --- ASSERT ---
        // Мы просто проверяем, что метод сохранения был вызван. Всё.
        _mockFeatureRepo.Verify(r => r.UpsertFeaturesAsync(featuresToReturn), Times.Once);
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
    }
}