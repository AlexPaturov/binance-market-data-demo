using BinanceDataCollector.Application.Interfaces;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Считает технические индикаторы (признаки) по свечам и складывает их в витрину
/// `Ohlcv_Features`. Работает инкрементально: берёт свечи со статусом 'new', считает,
/// помечает 'processed'.
///
/// Работу находит статус свечи, а не время: пересчитанная свеча снова становится 'new'
/// (см. `sp_aggregate_dirty_minutes`), и индикаторы по ней пересчитываются заново.
/// Порядок вызовов задаёт <see cref="FeatureCalculationService"/>.
/// </summary>
public class FeatureCalculatorWorker
{
    private readonly ILogger<FeatureCalculatorWorker> _logger;
    private readonly IOhlcvRepository  _ohlcvRepository;
    private readonly IFeatureRepository  _featureRepository;
    private readonly IIndicatorService  _indicatorService;
    private readonly IAnalysisRepository  _analysisRepository;

    // Конфигурация воркера

    /// <summary>
    /// Сколько свечей обрабатывать за один цикл. В обычном режиме поток — десятки свечей
    /// в минуту, но после импорта архивов пересчитанных свечей сотни тысяч: при пачке
    /// в 100 хвост в 395 тыс. разбирался бы 5.5 суток (ночь на 14.07.2026).
    /// </summary>
    private const int BatchSize = 2000;

    /// <summary>
    /// Прогрев индикаторов, в свечах. RSI(14) и MACD(26/9) сходятся в пределах ~150 баров;
    /// 400 даёт запас.
    /// </summary>
    private const int WarmupPeriod = 400;

    public FeatureCalculatorWorker(
        ILogger<FeatureCalculatorWorker> logger,
        IOhlcvRepository ohlcvRepository,
        IFeatureRepository  featureRepository,
        IIndicatorService  indicatorService,
        IAnalysisRepository  analysisRepository)
    {
        _logger = logger;
        _ohlcvRepository = ohlcvRepository;
        _featureRepository = featureRepository;
        _indicatorService = indicatorService;
        _analysisRepository = analysisRepository;
    }

    /// <summary>
    /// Обрабатывает одну пачку свечей. Возвращает сколько свечей взято в работу;
    /// 0 — новых свечей нет.
    /// </summary>
    public async Task<int> ProcessNextBatchAsync(CancellationToken stoppingToken)
    {
        // 1. "Резервируем" и получаем новую порцию работы (свечи со статусом 'new')
        var newKlinesToProcess = (await _ohlcvRepository.ClaimNewKlinesForProcessingAsync(BatchSize)).ToList();

        if (newKlinesToProcess.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("Received {Count} new klines for processing.", newKlinesToProcess.Count);

        var klinesBySymbol = newKlinesToProcess.GroupBy(k => k.Symbol);

        foreach (var group in klinesBySymbol)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var symbol = group.Key;
            var newKlinesForSymbol = group.ToList();

            try
            {
                // 2. Подгружаем "хвост" истории для "прогрева" индикаторов
                var firstNewTime = newKlinesForSymbol.Min(k => k.OpenTime);
                var historyKlines = await _ohlcvRepository.GetWarmupKlinesAsync(symbol, firstNewTime, WarmupPeriod);
                var allKlines = historyKlines.Concat(newKlinesForSymbol).OrderBy(k => k.OpenTime);

                // 3. Рассчитываем индикаторы на основе свечей
                var features = _indicatorService.CalculateAll(symbol, allKlines).ToList();

                // 4. Обогащаем данные индикатором CVD, который считается по тикам
                var cvdStartTime = DateTimeOffset.FromUnixTimeMilliseconds(firstNewTime).DateTime;
                var cvdEndTime = DateTimeOffset.FromUnixTimeMilliseconds(newKlinesForSymbol.Max(k => k.OpenTime)).DateTime.AddMinutes(1);
                var cvdByTime = (await _analysisRepository.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime))
                    .ToDictionary(c => c.OpenTime, c => c.Cvd);

                foreach (var feature in features)
                {
                    if (cvdByTime.TryGetValue(feature.OpenTime, out var cvd)) feature.Cvd = cvd;
                }

                // 5. Сохраняем только НОВЫЕ признаки (отсекаем "прогрев")
                var featuresToSave = features.Where(f => f.OpenTime >= firstNewTime).ToList();
                if (featuresToSave.Any())
                {
                    await _featureRepository.UpsertFeaturesAsync(featuresToSave);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Symbol}] Error calculating features for symbol.", symbol);
                // Падение одного символа не роняет пачку: остальные считаются дальше.
            }
        }

        // 6. Помечаем нашу порцию работы как полностью выполненную.
        // Передаём свечи целиком: ключ составной (Symbol, OpenTime), пометка
        // только по времени задела бы свечи других символов за ту же минуту.
        await _ohlcvRepository.MarkKlinesAsProcessedAsync(newKlinesToProcess);

        _logger.LogInformation("Successfully processed {Count} klines.", newKlinesToProcess.Count);

        return newKlinesToProcess.Count;
    }
}
