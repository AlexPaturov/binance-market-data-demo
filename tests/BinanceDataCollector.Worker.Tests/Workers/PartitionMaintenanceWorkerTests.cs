using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Worker.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BinanceDataCollector.Worker.Tests.Workers;

public class PartitionMaintenanceWorkerTests
{
    private readonly Mock<ITradeRepository> _repo = new();

    private PartitionMaintenanceWorker CreateWorker() => new(
        _repo.Object,
        Options.Create(new RetentionSettings()),
        NullLogger<PartitionMaintenanceWorker>.Instance);

    /// <summary>
    /// Пока backfill в полёте, печати не трогаем: месяц может ещё докачиваться,
    /// а «нет грязных минут» в паузе между пачками — ложный сигнал.
    /// </summary>
    [Fact]
    public async Task Reconcile_LeavesSealsUntouched_WhenImportInFlight()
    {
        _repo.Setup(r => r.IsArchiveImportInFlightAsync()).ReturnsAsync(true);

        await CreateWorker().ReconcileMonthSealsAsync();

        _repo.Verify(r => r.GetSealCandidateMonthsAsync(), Times.Never);
        _repo.Verify(r => r.UpsertMonthSealAsync(It.IsAny<DateOnly>()), Times.Never);
        _repo.Verify(r => r.DeleteMonthSealAsync(It.IsAny<DateOnly>()), Times.Never);
    }

    /// <summary>
    /// Backfill не идёт: полный месяц запечатывается, неполный — распечатывается
    /// (регрессировал — снова появилась работа или дыра в покрытии).
    /// </summary>
    [Fact]
    public async Task Reconcile_SealsCompleteMonths_AndUnsealsIncomplete()
    {
        var complete = new DateOnly(2026, 3, 1);
        var incomplete = new DateOnly(2026, 4, 1);

        _repo.Setup(r => r.IsArchiveImportInFlightAsync()).ReturnsAsync(false);
        _repo.Setup(r => r.GetSealCandidateMonthsAsync())
            .ReturnsAsync(new[] { complete, incomplete });
        _repo.Setup(r => r.IsMonthDataCompleteAsync(complete)).ReturnsAsync(true);
        _repo.Setup(r => r.IsMonthDataCompleteAsync(incomplete)).ReturnsAsync(false);

        await CreateWorker().ReconcileMonthSealsAsync();

        _repo.Verify(r => r.UpsertMonthSealAsync(complete), Times.Once);
        _repo.Verify(r => r.DeleteMonthSealAsync(incomplete), Times.Once);
        _repo.Verify(r => r.UpsertMonthSealAsync(incomplete), Times.Never);
        _repo.Verify(r => r.DeleteMonthSealAsync(complete), Times.Never);
    }
}
