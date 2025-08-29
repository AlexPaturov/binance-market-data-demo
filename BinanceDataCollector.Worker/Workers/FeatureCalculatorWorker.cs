using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Analytics;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Фоновый сервис, который отвечает за расчет всех технических индикаторов (признаков)
/// и сохранение их в "витрину данных" Ohlcv_Features.
/// </summary>
public class FeatureCalculatorWorker : BackgroundService
{
    private readonly ILogger<FeatureCalculatorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Конфигурация воркера
    private readonly TimeSpan _calculationInterval = TimeSpan.FromMinutes(5);
    private const int WarmupPeriod = 500; // Сколько свечей из прошлого нужно для "прогрева" индикаторов

    public FeatureCalculatorWorker(ILogger<FeatureCalculatorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер-калькулятор признаков (индикаторов) запущен.");
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); // Первоначальная задержка

        while (!stoppingToken.IsCancellationRequested)
        {
            await DoWorkAsync(stoppingToken); // Вызываем основную логику

            _logger.LogInformation("--- Расчет индикаторов завершен. Следующий запуск через {Interval}. ---", _calculationInterval);
            await Task.Delay(_calculationInterval, stoppingToken);
        }
    }

    public async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("--- Начинаем плановый расчет индикаторов ---");
        using (var scope = _serviceProvider.CreateScope())
        {
            var symbolRepo = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
            var activeSymbols = await symbolRepo.GetActiveSymbolsAsync();

            _logger.LogInformation("Обнаружено {Count} активных символов для расчета.", activeSymbols.Count());

            foreach (var symbol in activeSymbols)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await ProcessSymbolFeaturesAsync(scope, symbol, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Symbol}] Непредвиденная ошибка при расчете признаков.", symbol);
                }
            }
        }
    }

    //private async Task ProcessSymbolFeaturesAsync(IServiceScope scope, string symbol, CancellationToken stoppingToken)
    //{
    //    var ohlcvRepo = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>();
    //    var featureRepo = scope.ServiceProvider.GetRequiredService<IFeatureRepository>();
    //    var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
    //    var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();

    //    _logger.LogDebug("[{Symbol}] Шаг 1: Получаем время последней рассчитанной свечи...", symbol);
    //    var lastFeatureTime = await featureRepo.GetLastFeatureTimeAsync(symbol);

    //    _logger.LogDebug("[{Symbol}] Последнее время: {Time}", symbol, lastFeatureTime);

    //    var startTime = (lastFeatureTime.HasValue ? lastFeatureTime.Value : new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    //    _logger.LogDebug("[{Symbol}] Шаг 2: Получаем свечи с warmup-периодом ({Warmup}) начиная с {StartTime}...", symbol, WarmupPeriod, startTime);

    //    var klines = (await ohlcvRepo.GetKlinesWithWarmupAsync(symbol, startTime, WarmupPeriod)).ToList();
    //    _logger.LogDebug("[{Symbol}] Получено {Count} свечей из базы.", symbol, klines.Count);

    //    // --- КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ ЗДЕСЬ ---
    //    // Ищем хотя бы одну свечу, время которой СТРОГО БОЛЬШЕ, чем время последнего расчета.
    //    // Это более надежная проверка, чем простое сравнение количества.
    //    if (!klines.Any(k => k.OpenTime > (lastFeatureTime ?? 0)))
    //    {
    //        _logger.LogInformation("[{Symbol}] Не найдено НОВЫХ свечей (OpenTime > {lastFeatureTime}) для расчета. Пропускаем.", symbol, lastFeatureTime);
    //        return;
    //    }
    //    _logger.LogDebug("[{Symbol}] ПРОВЕРКА 1 ПРОЙДЕНА: Найдены новые свечи.", symbol);

    //    _logger.LogDebug("[{Symbol}] Шаг 3: Рассчитываем индикаторы на основе свечей...", symbol);
    //    var features = indicatorService.CalculateAll(symbol, klines).ToList();
    //    _logger.LogDebug("[{Symbol}] Рассчитано {Count} объектов FeatureData.", symbol, features.Count);

    //    // 4. Отдельно рассчитываем CVD по тиковым данным для нужного диапазона
    //    var firstNewKline = klines.FirstOrDefault(f => f.OpenTime > (lastFeatureTime ?? 0));
    //    if (firstNewKline != null)
    //    {
    //        var cvdStartTime = DateTimeOffset.FromUnixTimeMilliseconds(firstNewKline.OpenTime).DateTime;
    //        var cvdEndTime = DateTime.UtcNow;
    //        var cvdData = (await analysisRepo.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime)).ToList();

    //        // "Обогащаем" наши данные значениями CVD
    //        foreach (var feature in features)
    //        {
    //            var cvdPoint = cvdData.LastOrDefault(c => c.OpenTime == feature.OpenTime);
    //            if (cvdPoint != null)
    //            {
    //                feature.Cvd = cvdPoint.Cvd;
    //            }
    //        }
    //    }

    //    _logger.LogDebug("[{Symbol}] Шаг 4: Фильтруем warmup-период...", symbol);
    //    // Отбираем только те признаки, время которых СТРОГО БОЛЬШЕ, чем время последнего сохранения
    //    var finalFeaturesToSave = features.Where(f => f.OpenTime > (lastFeatureTime ?? 0)).ToList();
    //    _logger.LogDebug("[{Symbol}] Найдено {Count} финальных признаков для сохранения.", symbol, finalFeaturesToSave.Count);

    //    if (finalFeaturesToSave.Any())
    //    {
    //        _logger.LogDebug("[{Symbol}] ПРОВЕРКА 2 ПРОЙДЕНА: Вызываем UpsertFeaturesAsync...", symbol);
    //        await featureRepo.UpsertFeaturesAsync(finalFeaturesToSave);
    //        _logger.LogInformation("[{Symbol}] Успешно рассчитано и сохранено {Count} новых точек с признаками.", symbol, finalFeaturesToSave.Count);
    //    }
    //    else
    //    {
    //        _logger.LogWarning("[{Symbol}] ПРОВЕРКА 2 НЕ ПРОЙДЕНА: После расчета и фильтрации не осталось новых признаков для сохранения.", symbol);
    //    }
    //}

    private async Task ProcessSymbolFeaturesAsync(IServiceScope scope, string symbol, CancellationToken stoppingToken)
    {
        var ohlcvRepo = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>();
        var featureRepo = scope.ServiceProvider.GetRequiredService<IFeatureRepository>();
        var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();

        _logger.LogDebug("[{Symbol}] Получаем ВСЕ свечи из базы...", symbol);

        // --- ГЛАВНОЕ ИЗМЕНЕНИЕ ---
        // Мы больше не пытаемся быть умными. Мы просто берем ВСЕ свечи.
        // IOhlcvRepository должен иметь метод GetAllBySymbolAsync
        var klines = (await ohlcvRepo.GetAllBySymbolAsync(symbol)).ToList();

        if (!klines.Any())
        {
            _logger.LogInformation("[{Symbol}] Нет свечей для обработки.", symbol);
            return;
        }

        _logger.LogDebug("[{Symbol}] Получено {Count} свечей. Рассчитываем индикаторы...", symbol, klines.Count);
        var features = indicatorService.CalculateAll(symbol, klines).ToList();

        // ... (расчет и обогащение CVD, этот блок можно оставить) ...

        if (features.Any())
        {
            _logger.LogInformation("[{Symbol}] Сохраняем {Count} записей с признаками.", symbol, features.Count);
            await featureRepo.UpsertFeaturesAsync(features);
        }
    }
}
