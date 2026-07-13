using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Ротация партиций по размеру диска, ежедневно.
///
/// Создаёт партиции на текущий и следующий месяц, затем — пока суммарный размер
/// партиционированных данных выше порога — дропает самый старый месяц во всех
/// таблицах сразу (тики, свечи, индикаторы, отчёты о качестве).
///
/// Окно задаётся размером, а не календарём: реальные месячные партиции различаются
/// в 4.6 раза (32–148 ГБ), поэтому «держим N месяцев» непредсказуемо в байтах.
/// См. docs/adr/0007-size-based-retention-and-unified-partitioning.md
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

    private static double Gb(long bytes) => bytes / (1024.0 * 1024 * 1024);
}
