using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Worker.Workers;

public class HistoricalAuditorWorker : BackgroundService
{
    private readonly ILogger<HistoricalAuditorWorker> _logger;
    private readonly IServiceProvider _serviceProvider;

    //private readonly TimeSpan _auditInterval = TimeSpan.FromHours(12);
    private readonly TimeSpan _auditInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _failedBlockRetryInterval = TimeSpan.FromDays(1);
    private const int MaxRetries = 10;
    private const int BatchSize = 10; // Сколько блоков обрабатывать за один цикл

    public HistoricalAuditorWorker(ILogger<HistoricalAuditorWorker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Воркер исторического аудита запущен.");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

                // 1. Генерируем новые "задачи" для аудита
                await auditRepo.GenerateNewAuditBlocksAsync();

                // 2. Получаем порцию работы
                var blocksToProcess = await auditRepo.GetBlocksToProcessAsync(MaxRetries, BatchSize);

                if (!blocksToProcess.Any())
                {
                    _logger.LogInformation("Нет новых блоков для исторического аудита.");
                }
                else
                {
                    _logger.LogInformation("Получено {Count} блоков для проверки.", blocksToProcess.Count());
                    foreach (var block in blocksToProcess)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await ProcessBlockAsync(scope, block, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в главном цикле исторического аудитора.");
            }

            await Task.Delay(_auditInterval, stoppingToken);
        }
    }

    private async Task ProcessBlockAsync(IServiceScope scope, AuditBlock block, CancellationToken stoppingToken)
    {
        var analysisRepo = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
        // ... другие репозитории ...

        var symbol = block.Symbol;
        var blockStart = block.BlockStartDate;
        var blockEnd = blockStart.AddDays(3);

        try
        {
            // 3. Ищем дыры ТОЛЬКО в этом блоке
            var gaps = await analysisRepo.FindGapsInWindowAsync(symbol, blockStart, blockEnd);

            bool allGapsFilled = true;
            if (gaps.Any())
            {
                _logger.LogWarning("[{Symbol}] В блоке от {Date} найдено {Count} дыр. Начинаем заполнение.",
                    symbol, blockStart.ToShortDateString(), gaps.Count());

                // 4. Заполняем каждую дыру
                foreach (var gap in gaps)
                {
                    bool success = await FillGapAsync(scope, symbol, gap.GapStart, gap.GapEnd, stoppingToken);
                    if (!success)
                    {
                        allGapsFilled = false;
                        break; // Прерываем обработку этого блока, если хоть одна дыра не заполнилась
                    }
                }
            }

            // 5. Обновляем статус блока
            if (allGapsFilled)
            {
                await auditRepo.UpdateBlockStatusAsync(symbol, blockStart, "Completed", false);
                _logger.LogInformation("[{Symbol}] Блок от {Date} успешно проверен и помечен как 'Completed'.",
                    symbol, blockStart.ToShortDateString());
            }
            else
            {
                await auditRepo.UpdateBlockStatusAsync(symbol, blockStart, "Failed", true);
                _logger.LogError("[{Symbol}] Не удалось заполнить все дыры в блоке от {Date}. Блок помечен как 'Failed'.",
                    symbol, blockStart.ToShortDateString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Критическая ошибка при обработке блока от {Date}.",
                symbol, blockStart.ToShortDateString());
            await auditRepo.UpdateBlockStatusAsync(symbol, blockStart, "Failed", true);
        }
    }

    // Вспомогательный метод FillGapAsync остается почти таким же,
    // но теперь он должен возвращать bool (успех/неудача)
    private async Task<bool> FillGapAsync(IServiceScope scope, string symbol, long startMs, long endMs, CancellationToken stoppingToken)
    {
        // ... (логика с FetchResult, ApiLimit и т.д.) ...
        // В конце, если произошла GeneralError или другая ошибка, возвращаем false.
        // Если все данные скачаны, возвращаем true.
        return true; // Заглушка
    }
}
