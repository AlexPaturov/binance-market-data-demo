using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Обслуживание партиций: ротация по размеру диска (раз в сутки) и эвакуация закрытых
/// месяцев на холодный диск (раз в час) — разные задачи с разным ритмом.
///
/// Ротация: создаёт партиции на текущий и следующий месяц, затем — пока суммарный
/// размер партиционированных данных выше порога — дропает самый старый месяц во всех
/// таблицах сразу (тики, свечи, индикаторы, отчёты о качестве).
///
/// Окно задаётся размером, а не календарём: реальные месячные партиции различаются
/// в 4.6 раза (32–148 ГБ), поэтому «держим N месяцев» непредсказуемо в байтах.
/// См. docs/adr/0007-size-based-retention-and-unified-partitioning.md
///
/// Эвакуация: закрытые месяцы Trades переезжают с горячего SSD на холодный HDD
/// (`sp_evacuate_next_cold_partition`, миграция 013) — SSD держит только активные
/// месяцы, шпиндель получает единственную нагрузку, в которой он хорош:
/// последовательную запись.
/// </summary>
public class PartitionMaintenanceWorker
{
    private readonly ITradeRepository _tradeRepository;
    private readonly RetentionSettings _retention;
    private readonly ILogger<PartitionMaintenanceWorker> _logger;

    public PartitionMaintenanceWorker(
        ITradeRepository tradeRepository,
        IOptions<RetentionSettings> retention,
        ILogger<PartitionMaintenanceWorker> logger)
    {
        _tradeRepository = tradeRepository;
        _retention = retention.Value;
        _logger = logger;
    }

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
    public async Task RotatePartitionsAsync()
    {
        var before = await _tradeRepository.GetPartitionedSizeBytesAsync();

        _logger.LogInformation(
            "Partition rotation started. Size: {SizeGb:F1} GB, threshold: {ThresholdGb} GB, keeping at least {Months} months.",
            Gb(before), _retention.MaxPartitionedGigabytes, _retention.MinMonthsToKeep);

        await _tradeRepository.RotatePartitionsAsync(
            _retention.MaxPartitionedBytes, _retention.MinMonthsToKeep);

        var after = await _tradeRepository.GetPartitionedSizeBytesAsync();
        var floorMs = await _tradeRepository.GetRetentionFloorMsAsync();

        if (after < before)
        {
            // Дроп данных — событие, которое должно быть видно в логах без раскопок.
            _logger.LogWarning(
                "Partition rotation freed {FreedGb:F1} GB. Size: {AfterGb:F1} GB. Retention floor is now {Floor:yyyy-MM}.",
                Gb(before - after), Gb(after), DateTimeOffset.FromUnixTimeMilliseconds(floorMs).UtcDateTime);
        }
        else
        {
            _logger.LogInformation(
                "Partition rotation complete, nothing dropped. Size: {SizeGb:F1} GB.", Gb(after));
        }
    }

    /// <summary>
    /// Переносит на холодный диск одну партицию — старейший закрытый месяц тиков.
    ///
    /// Ритм — час, а не сутки: при импорте архивов горячий диск набивается за часы,
    /// и суточной уборки не хватает (17.07.2026 — SSD ушёл на 78% за одну ночь импорта).
    ///
    /// Одна партиция за запуск, а не сколько получится: месяц весит 50–150 ГБ и едет
    /// на HDD десятки минут. Переезд по одной удерживает запуск заметно короче часа,
    /// поэтому следующий не встаёт в очередь за блокировкой, а остаток разбирается
    /// на следующих часах — торопиться некуда, каждый переезд освобождает свои
    /// сто гигабайт сразу.
    /// </summary>
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task EvacuateNextColdPartitionAsync()
    {
        var partition = await _tradeRepository.EvacuateNextColdPartitionAsync();

        if (partition is null)
        {
            _logger.LogInformation("Nothing to evacuate: no closed month left on the hot disk.");
            return;
        }

        // Переезд гигабайтов — событие, которое должно быть видно в логах без раскопок.
        _logger.LogWarning("Partition {Partition} evacuated to the cold tablespace.", partition);
    }

    private static double Gb(long bytes) => bytes / (1024.0 * 1024 * 1024);
}
