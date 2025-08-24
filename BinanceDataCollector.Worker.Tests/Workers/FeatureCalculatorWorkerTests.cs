using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Moq;
using Serilog;

namespace BinanceDataCollector.Worker.Tests.Workers;

public class FeatureCalculatorWorkerTests : IDisposable
{
    // --- Секция моков (наши "актеры-дублеры"), это и есть изоляция ---
    private readonly Mock<ILogger<FeatureCalculatorWorker>> _mockLogger;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;

    // Моки для сервисов, которые использует воркер
    private readonly Mock<ITrackedSymbolRepository> _mockSymbolRepo;
    private readonly Mock<IOhlcvRepository> _mockOhlcvRepo;
    private readonly Mock<IFeatureRepository> _mockFeatureRepo;
    private readonly Mock<IAnalysisRepository> _mockAnalysisRepo;
    private readonly Mock<IIndicatorService> _mockIndicatorService; // Мокаем даже наш калькулятор

    private readonly ILogger<FeatureCalculatorWorker> _testLogger; // Логгер для самого теста

    public FeatureCalculatorWorkerTests() 
    {
        #region --- Настройка Serilog ---
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Seq("http://localhost:5341") // TODO to get from settings
            .Enrich.FromLogContext()
            .CreateLogger();

        var loggerFactory = new LoggerFactory().AddSerilog();
        _testLogger = loggerFactory.CreateLogger<FeatureCalculatorWorker>(); 
        #endregion

        // --- Подготовка моков ---
        _mockLogger = new Mock<ILogger<FeatureCalculatorWorker>>(); // Этот мок мы передадим в воркер

        // --- 1. Подготовка всех моков ---
        _mockLogger = new Mock<ILogger<FeatureCalculatorWorker>>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>(); 
        _mockScope = new Mock<IServiceScope>();

        // Моки для всех репозиториев и сервисов
        _mockSymbolRepo = new Mock<ITrackedSymbolRepository>();
        _mockOhlcvRepo = new Mock<IOhlcvRepository>();
        _mockFeatureRepo = new Mock<IFeatureRepository>();
        _mockAnalysisRepo = new Mock<IAnalysisRepository>();
        _mockIndicatorService = new Mock<IIndicatorService>();

        // --- 2. НАСТРАИВАЕМ ПРАВИЛЬНУЮ ЦЕПОЧКУ DI ---

        // Когда BackgroundService (или любой другой код) запрашивает IServiceScopeFactory
        // у главного Service Provider, мы возвращаем наш мок ФАБРИКИ.
        _mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);

        // Когда у мока ФАБРИКИ вызывают ее собственный метод CreateScope(),
        // мы возвращаем наш мок SCOPE. Это НЕ метод-расширение.
        _mockScopeFactory
            .Setup(factory => factory.CreateScope())
            .Returns(_mockScope.Object);

        // Когда внутри SCOPE запрашивают его собственный Service Provider,
        // мы возвращаем тот же самый мок IServiceProvider для простоты.
        _mockScope
            .Setup(scope => scope.ServiceProvider)
            .Returns(_mockServiceProvider.Object);

        // --- 3. Настраиваем IServiceProvider на возврат наших моков-сервисов ---
        // Теперь, когда любой код (внутри scope) попросит конкретный сервис, наш мок-провайдер вернет нужную "куклу".
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(ITrackedSymbolRepository))).Returns(_mockSymbolRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IOhlcvRepository))).Returns(_mockOhlcvRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IFeatureRepository))).Returns(_mockFeatureRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IAnalysisRepository))).Returns(_mockAnalysisRepo.Object);
        _mockServiceProvider.Setup(sp => sp.GetService(typeof(IIndicatorService))).Returns(_mockIndicatorService.Object);
    }

    // Вспомогательный метод для генерации свечей
    private List<Ohlcv> GenerateKlines(int count)
    {
        var klines = new List<Ohlcv>();
        for (int i = 0; i < count; i++) { klines.Add(new Ohlcv { Symbol = "BTCUSDT", OpenTime = i }); }
        return klines;
    }


    // --- ТЕСТЫ ---

    [Fact]
    public async Task ExecuteAsync_WhenNewKlinesExist_CalculatesAndSavesFeatures()
    {
        // --- ARRANGE ---
        // 1. "Обучаем" моки, что они должны возвращать
        _mockSymbolRepo.Setup(r => r.GetActiveSymbolsAsync()).ReturnsAsync(new List<string> { "BTCUSDT" });
        _mockFeatureRepo.Setup(r => r.GetLastFeatureTimeAsync("BTCUSDT")).ReturnsAsync(10L); // Последний раз считали свечу #10
        _mockOhlcvRepo
            .Setup(r => r.GetKlinesWithWarmupAsync("BTCUSDT", 11L, It.IsAny<int>()))
            .ReturnsAsync(GenerateKlines(5)); // Нашли 5 новых свечей

        // Наш "калькулятор" вернет 2 рассчитанных признака
        _mockIndicatorService
            .Setup(s => s.CalculateAll("BTCUSDT", It.IsAny<IEnumerable<Ohlcv>>()))
            .Returns(new List<FeatureData> { new FeatureData { OpenTime = 11 }, new FeatureData { OpenTime = 12 } });

        // Создаем экземпляр нашего воркера
        var worker = new FeatureCalculatorWorker(_mockLogger.Object, _mockServiceProvider.Object);

        // Создаем токен отмены, который сработает через короткое время, чтобы тест не длился вечно
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // --- ACT ---
        // Запускаем воркер
        await worker.StartAsync(cts.Token);
        await Task.Delay(1000); // Даем ему немного времени поработать
        await worker.StopAsync(cts.Token);

        // --- ASSERT ---
        // Проверяем, что метод сохранения был вызван РОВНО ОДИН РАЗ
        _mockFeatureRepo.Verify(r => r.UpsertFeaturesAsync(It.IsAny<IEnumerable<FeatureData>>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoNewKlines_DoesNotSaveFeatures()
    {
        // --- ARRANGE ---
        // 1. "Обучаем" моки
        _mockSymbolRepo.Setup(r => r.GetActiveSymbolsAsync()).ReturnsAsync(new List<string> { "BTCUSDT" });
        _mockFeatureRepo.Setup(r => r.GetLastFeatureTimeAsync("BTCUSDT")).ReturnsAsync(10L);

        // Ключевое отличие: репозиторий НЕ НАШЕЛ новых свечей
        _mockOhlcvRepo
            .Setup(r => r.GetKlinesWithWarmupAsync("BTCUSDT", 11L, It.IsAny<int>()))
            .ReturnsAsync(new List<Ohlcv>()); // Возвращаем пустой список

        var worker = new FeatureCalculatorWorker(_mockLogger.Object, _mockServiceProvider.Object);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // --- ACT ---
        await worker.StartAsync(cts.Token);
        await Task.Delay(1000);
        await worker.StopAsync(cts.Token);

        // --- ASSERT ---
        // Проверяем, что метод сохранения НЕ БЫЛ ВЫЗВАН ВООБЩЕ
        _mockFeatureRepo.Verify(r => r.UpsertFeaturesAsync(It.IsAny<IEnumerable<FeatureData>>()), Times.Never);
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
    }
}
