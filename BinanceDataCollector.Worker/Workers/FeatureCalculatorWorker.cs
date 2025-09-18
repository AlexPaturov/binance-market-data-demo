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
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация воркера
    private const int BatchSize = 500;      // Сколько свечей обрабатывать за один цикл
    private const int WarmupPeriod = 2016000; // 200 недель - максимальный период для наших MA

    public FeatureCalculatorWorker(ILogger<FeatureCalculatorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    [Queue("default")] // Тоже задача среднего приоритета
    [DisableConcurrentExecution(20 * 60)] // Даем 20 минут на расчет
    public async Task CalculateFeaturesAsync()
    {
        using (_logger.TimedOperation("Плановый расчет признаков"))
        {
            try
            {
                _logger.LogInformation("--- Начинаем плановый расчет признаков ---");
                using var scope = _serviceProvider.CreateScope();

                var ohlcvRepo = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>();
                var featureRepo = scope.ServiceProvider.GetRequiredService<IFeatureRepository>();
                var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();
                var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();

                // 1. "Резервируем" и получаем новую порцию работы (свечи со статусом 'new')
                var newKlinesToProcess = (await ohlcvRepo.ClaimNewKlinesForProcessingAsync(BatchSize)).ToList();

                if (!newKlinesToProcess.Any())
                {
                    _logger.LogInformation("Нет новых свечей для расчета признаков. Цикл пропущен.");
                    return;
                }

                _logger.LogInformation("Получено {Count} новых свечей для обработки.", newKlinesToProcess.Count);

                var klinesBySymbol = newKlinesToProcess.GroupBy(k => k.Symbol);

                foreach (var group in klinesBySymbol)
                {
                    var symbol = group.Key;
                    var newKlinesForSymbol = group.ToList();

                    try
                    {
                        // 2. Подгружаем "хвост" истории для "прогрева" индикаторов
                        var firstNewTime = newKlinesForSymbol.Min(k => k.OpenTime);
                        var historyKlines = await ohlcvRepo.GetWarmupKlinesAsync(symbol, firstNewTime, WarmupPeriod);
                        var allKlines = historyKlines.Concat(newKlinesForSymbol).OrderBy(k => k.OpenTime);

                        // 3. Рассчитываем индикаторы на основе свечей
                        var features = indicatorService.CalculateAll(symbol, allKlines).ToList();

                        // 4. Обогащаем данные индикатором CVD, который считается по тикам
                        var cvdStartTime = DateTimeOffset.FromUnixTimeMilliseconds(firstNewTime).DateTime;
                        var cvdEndTime = DateTimeOffset.FromUnixTimeMilliseconds(newKlinesForSymbol.Max(k => k.OpenTime)).DateTime.AddMinutes(1);
                        var cvdData = (await analysisRepo.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime)).ToList();

                        foreach (var feature in features)
                        {
                            var cvdPoint = cvdData.FirstOrDefault(c => c.OpenTime == feature.OpenTime);
                            if (cvdPoint != null) feature.Cvd = cvdPoint.Cvd;
                        }

                        // 5. Сохраняем только НОВЫЕ признаки (отсекаем "прогрев")
                        var featuresToSave = features.Where(f => f.OpenTime >= firstNewTime).ToList();
                        if (featuresToSave.Any())
                        {
                            await featureRepo.UpsertFeaturesAsync(featuresToSave);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Symbol}] Ошибка при расчете признаков для символа.", symbol);
                        // В этой архитектуре мы не откатываем статус, а просто пробуем снова в след. цикле
                    }
                }

                // 6. Помечаем нашу порцию работы как полностью выполненную
                var processedTimes = newKlinesToProcess.Select(k => k.OpenTime).Distinct();
                await ohlcvRepo.MarkKlinesAsProcessedAsync(processedTimes);

                _logger.LogInformation("Успешно обработано {Count} свечей.", newKlinesToProcess.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Произошла непредвиденная ошибка в главном цикле FeatureCalculatorWorker.");
            }

        }
    }

    public async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("--- Начинаем плановый расчет признаков ---");
        using var scope = _serviceProvider.CreateScope();

        var ohlcvRepo = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>();
        var featureRepo = scope.ServiceProvider.GetRequiredService<IFeatureRepository>();
        var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();

        // 1. "Резервируем" и получаем новую порцию работы (свечи со статусом 'new')
        var newKlinesToProcess = (await ohlcvRepo.ClaimNewKlinesForProcessingAsync(BatchSize)).ToList();

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
                var historyKlines = await ohlcvRepo.GetWarmupKlinesAsync(symbol, firstNewTime, WarmupPeriod);
                var allKlines = historyKlines.Concat(newKlinesForSymbol).OrderBy(k => k.OpenTime);

                // 3. Рассчитываем индикаторы на основе свечей
                var features = indicatorService.CalculateAll(symbol, allKlines).ToList();

                // 4. Обогащаем данные индикатором CVD, который считается по тикам
                var cvdStartTime = DateTimeOffset.FromUnixTimeMilliseconds(firstNewTime).DateTime;
                var cvdEndTime = DateTimeOffset.FromUnixTimeMilliseconds(newKlinesForSymbol.Max(k => k.OpenTime)).DateTime.AddMinutes(1);
                var cvdData = (await analysisRepo.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime)).ToList();

                foreach (var feature in features)
                {
                    var cvdPoint = cvdData.FirstOrDefault(c => c.OpenTime == feature.OpenTime);
                    if (cvdPoint != null) feature.Cvd = cvdPoint.Cvd;
                }

                // 5. Сохраняем только НОВЫЕ признаки (отсекаем "прогрев")
                var featuresToSave = features.Where(f => f.OpenTime >= firstNewTime).ToList();
                if (featuresToSave.Any())
                {
                    await featureRepo.UpsertFeaturesAsync(featuresToSave);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Symbol}] Ошибка при расчете признаков для символа.", symbol);
                // В этой архитектуре мы не откатываем статус, а просто пробуем снова в след. цикле
            }
        }

        // 6. Помечаем нашу порцию работы как полностью выполненную
        var processedTimes = newKlinesToProcess.Select(k => k.OpenTime).Distinct();
        await ohlcvRepo.MarkKlinesAsProcessedAsync(processedTimes);

        _logger.LogInformation("Успешно обработано {Count} свечей.", newKlinesToProcess.Count);
    }
}