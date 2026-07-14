using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который отвечает за расчет всех технических индикаторов (признаков)
/// и сохранение их в "витрину данных" Ohlcv_Features.
/// Работает по инкрементальному принципу ("статусы + вотермарки").
/// Двигается по свечам
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
    /// 400 даёт запас. Больший прогрев не даёт ничего: колонки MA_1051200/MA_201600 пусты
    /// при любом лимите — столько истории не существует (см. docs/TECH_DEBT.md, п. 2), —
    /// а прежний лимит в 2 016 000 означал выборку всей истории символа каждый цикл.
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

    // Очередь приоритетного сервера: индикаторы идут следом за свечами и должны поспевать
    // за ними. В `default` расчёт делил воркеров с импортом архивов и голодал вместе с
    // агрегатором.
    [Queue("realtime")]
    [SkipWhenPreviousJobIsRunning]
    [DisableConcurrentExecution(30 * 60)] // Даем 20 минут на расчет
    public async Task CalculateFeaturesAsync()
    {
        using (_logger.TimedOperation("Scheduled feature calculation"))
        {
            try
            {
                // 1. "Резервируем" и получаем новую порцию работы (свечи со статусом 'new')
                var newKlinesToProcess = (await _ohlcvRepository.ClaimNewKlinesForProcessingAsync(BatchSize)).ToList();

                if (!newKlinesToProcess.Any())
                {
                    _logger.LogInformation("No new klines for feature calculation. Cycle skipped.");
                    return;
                }

                _logger.LogInformation("Received {Count} new klines for processing.", newKlinesToProcess.Count);

                var klinesBySymbol = newKlinesToProcess.GroupBy(k => k.Symbol);

                foreach (var group in klinesBySymbol)
                {
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
                        // В этой архитектуре мы не откатываем статус, а просто пробуем снова в след. цикле
                    }
                }

                // 6. Помечаем нашу порцию работы как полностью выполненную.
                // Передаём свечи целиком: ключ составной (Symbol, OpenTime), пометка
                // только по времени задела бы свечи других символов за ту же минуту.
                await _ohlcvRepository.MarkKlinesAsProcessedAsync(newKlinesToProcess);

                _logger.LogInformation("Successfully processed {Count} klines.", newKlinesToProcess.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in FeatureCalculatorWorker main loop.");
            }

        }
    }

    public async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("--- Starting scheduled feature calculation ---");

        // 1. "Резервируем" и получаем новую порцию работы (свечи со статусом 'new')
        var newKlinesToProcess = (await _ohlcvRepository.ClaimNewKlinesForProcessingAsync(BatchSize)).ToList();

        if (!newKlinesToProcess.Any())
        {
            _logger.LogInformation("Нет новых свечей для расчета признаков. Цикл пропущен.");
            return;
        }

        _logger.LogInformation("Получено {Count} новых свечей для обработки.", newKlinesToProcess.Count);

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
                var cvdData = (await _analysisRepository.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime)).ToList();

                foreach (var feature in features)
                {
                    var cvdPoint = cvdData.FirstOrDefault(c => c.OpenTime == feature.OpenTime);
                    if (cvdPoint != null) feature.Cvd = cvdPoint.Cvd;
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
                _logger.LogError(ex, "[{Symbol}] Ошибка при расчете признаков для символа.", symbol);
                // В этой архитектуре мы не откатываем статус, а просто пробуем снова в след. цикле
            }
        }

        // 6. Помечаем нашу порцию работы как полностью выполненную (по паре Symbol+OpenTime).
        await _ohlcvRepository.MarkKlinesAsProcessedAsync(newKlinesToProcess);

        _logger.LogInformation("Успешно обработано {Count} свечей.", newKlinesToProcess.Count);
    }
}