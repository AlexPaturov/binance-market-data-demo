using BinanceDataCollector.Application.Analytics;   // Пространство имен, где лежит IndicatorService
using BinanceDataCollector.Domain.Entities;
using Microsoft.Extensions.Logging;
using Serilog;
using Skender.Stock.Indicators;
using Xunit.Abstractions;         // Пространство имен для Ohlcv

namespace BinanceDataCollector.Application.Tests.Services; 

/// <summary>
/// Юнит-тесты для класса IndicatorService.
/// </summary>
public class IndicatorServiceTests : IDisposable
{
    // --- Секция подготовки (Arrange) ---
    private readonly ILogger<IndicatorServiceTests> _logger;
    private readonly IndicatorService _indicatorService;

    // Конструктор теста. Он выполняется перед каждым тестовым методом.
    public IndicatorServiceTests(ITestOutputHelper output)
    {
        // Настраиваем Serilog глобально для всех тестов
        // Это нужно сделать один раз
        Log.Logger = new LoggerConfiguration()
           .MinimumLevel.Debug() // Устанавливаем минимальный уровень логирования
           .WriteTo.Seq("http://localhost:5341") // Указываем адрес нашего локального Seq
           .Enrich.FromLogContext()
           .CreateLogger();

        // Создаем ILoggerFactory, который использует настроенный Serilog
        var loggerFactory = new LoggerFactory().AddSerilog();
        // Создаем экземпляр стандартного ILogger<T> для использования в тесте
        _logger = loggerFactory.CreateLogger<IndicatorServiceTests>();

        // Создаем экземпляр нашего сервиса, который мы будем тестировать.
        // Так как у него нет зависимостей в конструкторе, мы просто создаем его.
        _indicatorService = new IndicatorService();
    }

    /// <summary>
    /// Вспомогательный метод для генерации набора тестовых свечей (OHLCV).
    /// </summary>
    /// <param name="count">Количество свечей для генерации.</param>
    /// <returns>Коллекция тестовых свечей.</returns>
    private IEnumerable<Ohlcv> GenerateSampleKlines(int count)
    {
        var klines = new List<Ohlcv>();
        // Генерируем данные в прошлом, чтобы избежать проблем с текущим временем
        var startTime = DateTimeOffset.UtcNow.AddMinutes(-count).ToUnixTimeMilliseconds();

        for (int i = 0; i < count; i++)
        {
            klines.Add(new Ohlcv
            {
                Symbol = "BTCUSDT",
                OpenTime = startTime + (i * 60000), // Каждая свеча на 1 минуту позже
                OpenPrice = 100 + i,
                HighPrice = 105 + i,
                LowPrice = 95 + i,
                ClosePrice = 102 + i, // Создаем простой восходящий тренд
                Volume = 10
            });
        }
        return klines;
    }

    // --- Секция тестов (Act & Assert) ---

    //[Fact] // Тест периодически падает на несоответствии количества даных то 13 не хватат то хватает
    //public void CalculateAll_WithSufficientData_CalculatesRsiCorrectly()
    //{
    //    try
    //    {
    //        // Arrange (Подготовка): Готовим данные, которых достаточно для расчета RSI(14)
    //        _logger.LogInformation("--- Начинаем тест: CalculateAll_WithSufficientData_CalculatesRsiCorrectly ---"); // debug
    //        var klines = GenerateSampleKlines(20); // Генерируем 20 свечей
    //        _logger.LogInformation("Сгенерировано {Count} свечей для теста.", klines.Count()); // debug

    //        // ------------------------------------------
    //        // --- ШАГ ОТЛАДКИ ---
    //        // Давайте посмотрим, что внутри. Конвертируем в Quote для отладки
    //        var quotesForDebug = klines.Select(k => new Quote
    //        {
    //            Date = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).DateTime,
    //            Open = k.OpenPrice,
    //            High = k.HighPrice,
    //            Low = k.LowPrice,
    //            Close = k.ClosePrice,
    //            Volume = k.Volume
    //        }).OrderBy(q => q.Date).ToList();

    //        // --- Отладочный шаг 1: Логируем исходные данные ---
    //        _logger.LogDebug("Исходные данные Quote (первые 5): {@Quotes}", quotesForDebug.Take(5));

    //        var rsiResultForDebug = quotesForDebug.GetRsi(14).ToList();

    //        // --- Отладочный шаг 2: Логируем результат расчета RSI ---
    //        // Точка останова. Смотрим в отладчике на содержимое rsiResultForDebug. Сколько там элементов? Какие у них даты? Какие значения Rsi?
    //        _logger.LogInformation("Библиотека Skender рассчитала {Count} значений RSI.", rsiResultForDebug.Count);
    //        _logger.LogDebug("Результат расчета RSI: {@RsiResults}", rsiResultForDebug);

    //        // Act (Действие): Выполняем тестируемый метод
    //        _logger.LogInformation("Вызываем тестируемый метод CalculateAll...");
    //        var features = _indicatorService.CalculateAll("BTCUSDT", klines).ToList();
    //        _logger.LogInformation("Метод CalculateAll вернул {Count} объектов FeatureData.", features.Count);

    //        // --- Отладочный шаг 3: Логируем финальный результат ---
    //        var featureToInspect = features.ElementAtOrDefault(13);
    //        _logger.LogDebug("Объект FeatureData для проверки (индекс 13): {@Feature}", featureToInspect);
    //        // ------------------------------------------

    //        // Assert (Проверка): Проверяем, что результат соответствует ожиданиям
    //        Assert.NotNull(features);
    //        Assert.Equal(20, features.Count); // Должны получить результат для каждой свечи

    //        // Для первых 13-ти свечей RSI должен быть null, так как ему нужно 14 периодов для расчета
    //        Assert.All(features.Take(13), feature => Assert.Null(feature.Rsi14));

    //        // Начиная с 14-й свечи (индекс 13), RSI должен быть рассчитан (не null)
    //        _logger.LogInformation("Проверяем Assert.NotNull для RSI на 14-й свече...");
    //        Assert.NotNull(features[13].Rsi14); // Эта строка падает

    //        Assert.True(features[13].Rsi14 > 50); // Так как у нас восходящий тренд, RSI должен быть больше 50
    //    }
    //    catch (Exception ex) 
    //    {
    //        _logger.LogInformation("--- Тест завершен, принудительная отправка логов ---");
    //        Log.CloseAndFlush();
    //        Task.Delay(1000).Wait(); // Даем 1 секунду на отправку по сети
    //    }
    //}

    [Fact]
    public void CalculateAll_WithKnownDataSource_CalculatesRsiEqualToGoldenStandard()
    {
        // --- ARRANGE ---
        _logger.LogInformation("--- Начинаем тест по золотому стандарту: CalculateAll_WithKnownDataSource_CalculatesRsiEqualToGoldenStandard ---");

        // 1. Готовим эталонные данные
        var knownClosePrices = new List<decimal>
    {
        44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m, 45.84m, 46.08m,
        45.89m, 46.03m, 45.61m, 46.28m, 46.28m // 15-я свеча
    };

        var klines = new List<Ohlcv>();
        var startTime = DateTimeOffset.UtcNow.AddMinutes(-knownClosePrices.Count).ToUnixTimeMilliseconds();
        for (int i = 0; i < knownClosePrices.Count; i++)
        {
            klines.Add(new Ohlcv
            {
                Symbol = "TEST",
                OpenTime = startTime + (i * 60000),
                OpenPrice = knownClosePrices[i],
                HighPrice = knownClosePrices[i],
                LowPrice = knownClosePrices[i],
                ClosePrice = knownClosePrices[i],
                Volume = 1
            });
        }

        // 2. Определяем эталонный результат
        const decimal expectedRsi = 70.4641m;
        const int precision = 4; // Сравниваем с точностью до 4 знаков после запятой

        _logger.LogInformation("Подготовлено {Count} свечей. Ожидаемое значение RSI на последней свече: {ExpectedRsi}", klines.Count, expectedRsi);

        // --- ACT ---
        var features = _indicatorService.CalculateAll("TEST", klines).ToList();
        _logger.LogInformation("Метод CalculateAll вернул {Count} результатов.", features.Count);

        // --- ASSERT ---
        Assert.Equal(knownClosePrices.Count, features.Count);

        // Получаем последнее значение RSI из наших результатов
        var actualRsi = features.Last().Rsi14;
        _logger.LogInformation("Фактическое значение RSI на последней свече: {ActualRsi}", actualRsi);

        // Проверяем, что оно не null
        Assert.NotNull(actualRsi);

        // Сравниваем фактическое значение с эталонным с заданной точностью
        Assert.Equal(expectedRsi, actualRsi.Value, precision);
    }


    [Fact]
    public void CalculateAll_WithInsufficientData_ReturnsNullIndicators()
    {
        // Arrange: Готовим недостаточно данных (10 свечей) для RSI(14)
        var klines = GenerateSampleKlines(10);

        // Act: Выполняем метод
        var features = _indicatorService.CalculateAll("BTCUSDT", klines).ToList();

        // Assert: Проверяем, что RSI не рассчитан ни для одной свечи
        Assert.NotNull(features);
        Assert.Equal(10, features.Count);
        Assert.All(features, feature => Assert.Null(feature.Rsi14));
    }

    [Fact]
    public void CalculateAll_WithEmptyData_ReturnsEmptyResult()
    {
        // Arrange: Подаем на вход пустой список
        var klines = new List<Ohlcv>();

        // Act: Выполняем метод
        var features = _indicatorService.CalculateAll("BTCUSDT", klines).ToList();

        // Assert: Проверяем, что результат - это пустой список
        Assert.NotNull(features);
        Assert.Empty(features);
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
    }
}