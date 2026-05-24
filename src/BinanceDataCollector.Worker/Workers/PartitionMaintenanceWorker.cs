using BinanceDataCollector.Application.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Maintains the Trades table rolling 13-month partition window.
/// Creates current and next month partitions, drops the 14th oldest.
/// Runs daily on prod. Disabled during dev initial load phase.
/// </summary>
public class PartitionMaintenanceWorker
{
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<PartitionMaintenanceWorker> _logger;

    public PartitionMaintenanceWorker(ITradeRepository tradeRepository, ILogger<PartitionMaintenanceWorker> logger)
    {
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    [Queue("default")]
    public async Task RotatePartitionsAsync()
    {
        _logger.LogInformation("Partition rotation started.");
        await _tradeRepository.RotatePartitionsAsync();
        _logger.LogInformation("Partition rotation complete.");
    }
}