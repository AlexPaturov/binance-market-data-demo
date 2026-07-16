using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BinanceDataCollector.Worker.Tests.Common;

/// <summary>
/// Импорт на остатке ресурса: пачка вставки ждёт, пока лаг свечи не вернётся в норму,
/// и не дребезжит на границе порога (гистерезис).
/// </summary>
public sealed class ImportBackpressureTests
{
    private readonly Mock<IOhlcvRepository> _ohlcvRepo = new();

    private ImportBackpressure CreateBackpressure()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ohlcvRepo.Object);
        var provider = services.BuildServiceProvider();

        return new ImportBackpressure(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ImportBackpressure>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(10));
    }

    private static long NewestWithLag(TimeSpan lag) =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)lag.TotalMilliseconds;

    [Fact]
    public async Task Wait_ReturnsImmediately_WhenPipelineIsHealthy()
    {
        _ohlcvRepo.Setup(r => r.GetNewestCandleOpenTimeAsync())
            .ReturnsAsync(NewestWithLag(TimeSpan.FromSeconds(90)));

        await CreateBackpressure().WaitForPipelineHeadroomAsync(CancellationToken.None);

        _ohlcvRepo.Verify(r => r.GetNewestCandleOpenTimeAsync(), Times.Once);
    }

    /// <summary>
    /// Гистерезис: лаг упал ниже порога остановки (240 с), но ещё выше порога
    /// возобновления (150 с) — импорт продолжает ждать. Без зазора он дребезжал бы
    /// на границе: пачка — пауза — пачка.
    /// </summary>
    [Fact]
    public async Task Wait_HoldsUntilLagDropsBelowResumeThreshold()
    {
        var lags = new Queue<TimeSpan>(new[]
        {
            TimeSpan.FromSeconds(300),   // выше порога остановки — импорт встал
            TimeSpan.FromSeconds(200),   // между порогами — ждём дальше
            TimeSpan.FromSeconds(100)    // ниже порога возобновления — поехали
        });

        _ohlcvRepo.Setup(r => r.GetNewestCandleOpenTimeAsync())
            .ReturnsAsync(() => NewestWithLag(lags.Dequeue()));

        await CreateBackpressure().WaitForPipelineHeadroomAsync(CancellationToken.None);

        _ohlcvRepo.Verify(r => r.GetNewestCandleOpenTimeAsync(), Times.Exactly(3));
    }

    /// <summary>Пустая база — придерживать импорт не за чем: это бутстрап с нуля.</summary>
    [Fact]
    public async Task Wait_ReturnsImmediately_WhenNoCandlesExist()
    {
        _ohlcvRepo.Setup(r => r.GetNewestCandleOpenTimeAsync())
            .ReturnsAsync((long?)null);

        await CreateBackpressure().WaitForPipelineHeadroomAsync(CancellationToken.None);

        _ohlcvRepo.Verify(r => r.GetNewestCandleOpenTimeAsync(), Times.Once);
    }

    [Fact]
    public async Task Wait_Throws_WhenCancelledWhilePaused()
    {
        _ohlcvRepo.Setup(r => r.GetNewestCandleOpenTimeAsync())
            .ReturnsAsync(() => NewestWithLag(TimeSpan.FromSeconds(600)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateBackpressure().WaitForPipelineHeadroomAsync(cts.Token));
    }
}
