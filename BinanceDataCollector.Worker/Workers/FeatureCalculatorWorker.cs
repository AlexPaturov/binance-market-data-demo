
using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Interfaces;

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
        // Даем системе время на первоначальный сбор данных
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
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

            _logger.LogInformation("--- Расчет индикаторов завершен. Следующий запуск через {Interval}. ---", _calculationInterval);
            await Task.Delay(_calculationInterval, stoppingToken);
        }
    }

    private async Task ProcessSymbolFeaturesAsync(IServiceScope scope, string symbol, CancellationToken stoppingToken)
    {
        var ohlcvRepo = scope.ServiceProvider.GetRequiredService<IOhlcvRepository>(); 
        var featureRepo = scope.ServiceProvider.GetRequiredService<IFeatureRepository>();
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();

        
        _logger.LogDebug("[{Symbol}] Шаг 1: Получаем время последней рассчитанной свечи...", symbol);
        var lastFeatureTime = await featureRepo.GetLastFeatureTimeAsync(symbol); // 1. Находим время последней рассчитанной свечи, чтобы не делать лишнюю работу
        _logger.LogDebug("[{Symbol}] Последнее время: {Time}", symbol, lastFeatureTime);
        var startTime = lastFeatureTime ?? new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        
        _logger.LogDebug("[{Symbol}] Шаг 2: Получаем свечи с warmup-периодом ({Warmup}) начиная с {StartTime}...", symbol, WarmupPeriod, startTime);
        var klines = (await ohlcvRepo.GetKlinesWithWarmupAsync(symbol, startTime, WarmupPeriod)).ToList(); // 2. Получаем свечи с "прогревом" (warmup)
        _logger.LogInformation("[{Symbol}] Получено {Count} свечей из базы.", symbol, klines.Count); // TODO -> LogDebug

        if (klines.Count <= WarmupPeriod)
        {
            _logger.LogInformation("[{Symbol}] Недостаточно новых свечей для расчета. Пропускаем.", symbol);
            _logger.LogWarning("[{Symbol}] ПРОВЕРКА 1 НЕ ПРОЙДЕНА: Не найдено новых свечей. Выход.", symbol); // TODO -> LogDebug
            return;
        }
        _logger.LogInformation("[{Symbol}] ПРОВЕРКА 1 ПРОЙДЕНА: Найдены новые свечи.", symbol); // TODO -> LogDebug

        _logger.LogDebug("[{Symbol}] Шаг 3: Рассчитываем индикаторы на основе свечей...", symbol);
        var features = indicatorService.CalculateAll(symbol, klines).ToList(); // 3. Рассчитываем все индикаторы, основанные на свечах (RSI, MACD, MA)
        _logger.LogInformation("[{Symbol}] Рассчитано {Count} объектов FeatureData.", symbol, features.Count);  // TODO -> LogDebug

        // 4. Отдельно рассчитываем CVD по тиковым данным для нужного диапазона
        var firstNewKlineTime = features.FirstOrDefault(f => f.OpenTime >= startTime);
        if (firstNewKlineTime != null)
        {
            var cvdStartTime = DateTimeOffset.FromUnixTimeMilliseconds(firstNewKlineTime.OpenTime).DateTime;
            var cvdEndTime = DateTime.UtcNow;
            var cvdData = (await analysisRepo.GetCvdForOhlcvAsync(symbol, cvdStartTime, cvdEndTime)).ToList();

            // "Обогащаем" наши данные значениями CVD
            foreach (var feature in features)
            {
                var cvdPoint = cvdData.LastOrDefault(c => c.OpenTime == feature.OpenTime);
                if (cvdPoint != null)
                {
                    feature.Cvd = cvdPoint.Cvd;
                }
            }
        }

        _logger.LogDebug("[{Symbol}] Шаг 4: Фильтруем warmup-период...", symbol);
        var finalFeaturesToSave = features.Where(f => f.OpenTime >= startTime).ToList(); // 5. Отфильтровываем "прогревочный" период, чтобы не сохранять неполные данные
        _logger.LogInformation("[{Symbol}] Найдено {Count} финальных признаков для сохранения.", symbol, finalFeaturesToSave.Count); // TODO -> LogDebug

        if (finalFeaturesToSave.Any())
        {
            _logger.LogInformation("[{Symbol}] ПРОВЕРКА 2 ПРОЙДЕНА: Вызываем UpsertFeaturesAsync...", symbol); // TODO -> LogDebug
            await featureRepo.UpsertFeaturesAsync(finalFeaturesToSave); // 6. Сохраняем рассчитанные признаки в базу данных
            _logger.LogInformation("[{Symbol}] Успешно рассчитано и сохранено {Count} новых точек с признаками.",symbol, finalFeaturesToSave.Count); 
        }
        else
        {
            _logger.LogWarning("[{Symbol}] ПРОВЕРКА 2 НЕ ПРОЙДЕНА: Нет признаков для сохранения. Выход.", symbol);
        }
    }
}
