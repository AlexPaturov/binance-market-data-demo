using BinanceDataCollector.Application.Interfaces;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Ограничитель импорта по лагу свечи: реалтайм-конвейер получает диск первым,
/// импорт архивов работает на остатке.
///
/// Сигнал — возраст самой свежей свечи. Он напрямую измеряет «конвейер успевает»:
/// когда импорт отбирает у агрегации IOPS диска, свежие минуты разбираются дольше
/// и лаг растёт; когда импорт замолкает, агрегация догоняет и лаг падает.
/// </summary>
public sealed class ImportBackpressure : IImportBackpressure
{
    /// <summary>
    /// Выше — импорт встаёт. Здоровый лаг реалтайма — 60–130 с (свеча появляется после
    /// закрытия минуты, OpenTime — её начало), под импортом на проде лаг доходил до
    /// 260–390 с (замер 16.07.2026). Порог между этими режимами.
    /// </summary>
    private static readonly TimeSpan PauseAbove = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Ниже — импорт продолжает. Зазор с порогом остановки (гистерезис) не даёт импорту
    /// дребезжать на границе; ниже 150 с не опускаем — здоровый лаг сам по себе до ~130 с,
    /// более строгий порог держал бы импорт стоящим вечно.
    /// </summary>
    private static readonly TimeSpan ResumeBelow = TimeSpan.FromSeconds(150);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImportBackpressure> _logger;
    private readonly TimeSpan _pollInterval;

    public ImportBackpressure(
        IServiceScopeFactory scopeFactory,
        ILogger<ImportBackpressure> logger)
        : this(scopeFactory, logger, TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>Интервал опроса выведен в параметр для тестов: ждать штатные 15 с тест не может.</summary>
    public ImportBackpressure(
        IServiceScopeFactory scopeFactory,
        ILogger<ImportBackpressure> logger,
        TimeSpan pollInterval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    public async Task WaitForPipelineHeadroomAsync(CancellationToken cancellationToken)
    {
        var lag = await CandleLagAsync();
        if (lag <= PauseAbove) return;

        _logger.LogWarning(
            "Candle lag {LagSeconds:F0}s exceeds {PauseSeconds:F0}s — pausing import until the pipeline catches up.",
            lag.TotalSeconds, PauseAbove.TotalSeconds);

        do
        {
            await Task.Delay(_pollInterval, cancellationToken);
            lag = await CandleLagAsync();
        }
        while (lag > ResumeBelow);

        _logger.LogInformation(
            "Candle lag {LagSeconds:F0}s is back under {ResumeSeconds:F0}s — resuming import.",
            lag.TotalSeconds, ResumeBelow.TotalSeconds);
    }

    private async Task<TimeSpan> CandleLagAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var ohlcvRepository = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>();

        var newest = await ohlcvRepository.GetNewestCandleOpenTimeAsync();

        // Свечей нет вовсе — база пуста, придерживать импорт не за чем.
        if (newest is null) return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - newest.Value);
    }
}
