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
///
/// Эвакуацию закрытых месяцев на холодный диск этот воркер больше не делает — её
/// планирует сама БД через pg_cron (`sp_evacuate_next_cold_partition`, миграция 014).
/// Но решение «месяц закрыт» (печать `MonthSeal`, миграция 015) принимает здесь:
/// оно требует знания очереди импорта Hangfire, которого у pg_cron-функции нет.
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

    [Queue("maintenance")]
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
    /// Пересчитывает печати закрытых месяцев (`MonthSeal`) — точный критерий эвакуации.
    ///
    /// Решение принимается здесь, а не в pg_cron-функции, потому что требует знания,
    /// которого у SQL нет: идёт ли ещё backfill (очередь импорта Hangfire в другой БД).
    /// Пока хоть одна задача импорта в полёте — печати не трогаем: месяц может ещё
    /// докачиваться. Иначе для каждого прошлого месяца с покрытием: данные готовы
    /// (`fn_month_data_complete`) — ставим печать, иначе снимаем (месяц регрессировал).
    /// </summary>
    [Queue("maintenance")]
    [DisableConcurrentExecution(timeoutInSeconds: 30 * 60)]
    public async Task ReconcileMonthSealsAsync()
    {
        if (await _tradeRepository.IsArchiveImportInFlightAsync())
        {
            _logger.LogInformation("Import is in flight — month seals left unchanged.");
            return;
        }

        var sealedCount = 0;
        foreach (var month in await _tradeRepository.GetSealCandidateMonthsAsync())
        {
            if (await _tradeRepository.IsMonthDataCompleteAsync(month))
            {
                await _tradeRepository.UpsertMonthSealAsync(month);
                sealedCount++;
            }
            else
            {
                await _tradeRepository.DeleteMonthSealAsync(month);
            }
        }

        _logger.LogInformation("Month seal reconciliation complete. Sealed months: {Sealed}.", sealedCount);
    }

    private static double Gb(long bytes) => bytes / (1024.0 * 1024 * 1024);
}
