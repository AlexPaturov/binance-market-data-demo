using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAuditRepository
{
    Task GenerateNewAuditBlocksAsync();
    Task<IEnumerable<AuditBlock>> GetBlocksToProcessAsync(int maxRetries, int limit);
    Task UpdateBlockStatusAsync(string symbol, DateTime blockStartDate, string newStatus, bool incrementRetryCount);

}
