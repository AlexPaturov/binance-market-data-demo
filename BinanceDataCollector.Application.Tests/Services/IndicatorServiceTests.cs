using BinanceDataCollector.Application.Analytics;   // Пространство имен, где лежит IndicatorService
using BinanceDataCollector.Domain.Entities;         // Пространство имен для Ohlcv

namespace BinanceDataCollector.Application.Tests.Services; 

/// <summary>
/// Юнит-тесты для класса IndicatorService.
/// </summary>
public class IndicatorServiceTests
{
    // --- Секция подготовки (Arrange) ---

    private readonly IndicatorService _indicatorService;

    // Конструктор теста. Он выполняется перед каждым тестовым методом.
    public IndicatorServiceTests()
    {
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

    [Fact] // Атрибут xUnit, помечающий метод как тест
    public void CalculateAll_WithSufficientData_CalculatesRsiCorrectly()
    {
        // Arrange (Подготовка): Готовим данные, которых достаточно для расчета RSI(14)
        var klines = GenerateSampleKlines(20); // Генерируем 20 свечей

        // Act (Действие): Выполняем тестируемый метод
        var features = _indicatorService.CalculateAll("BTCUSDT", klines).ToList();

        // Assert (Проверка): Проверяем, что результат соответствует ожиданиям
        Assert.NotNull(features);
        Assert.Equal(20, features.Count); // Должны получить результат для каждой свечи

        // Для первых 13-ти свечей RSI должен быть null, так как ему нужно 14 периодов для расчета
        Assert.All(features.Take(13), feature => Assert.Null(feature.Rsi14));

        // Начиная с 14-й свечи (индекс 13), RSI должен быть рассчитан (не null)
        Assert.NotNull(features[13].Rsi14);
        Assert.True(features[13].Rsi14 > 50); // Так как у нас восходящий тренд, RSI должен быть больше 50
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
}