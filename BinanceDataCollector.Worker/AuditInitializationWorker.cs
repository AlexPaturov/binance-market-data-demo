using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Common;
using Hangfire;

namespace BinanceDataCollector.Worker;

/// <summary>
/// Первичное создание вотермарки для каждого символа
/// </summary>
public class AuditInitializationWorker
{
    private readonly IHistoricalAuditRepository _auditRepo;
    private readonly ILogger<AuditInitializationWorker> _logger;

    public AuditInitializationWorker(IHistoricalAuditRepository auditRepo, ILogger<AuditInitializationWorker> logger)
    {
        _auditRepo = auditRepo;
        _logger = logger;
    }

    [Queue("default")] // Это некритичная, фоновая задача
    public async Task CreateWatermarksForNewSymbolsAsync()
    {
        using (_logger.TimedOperation("Инициализация аудита для новых символов"))
        {
            await _auditRepo.InitializeAuditForNewSymbolsAsync();
        }
    }
}
