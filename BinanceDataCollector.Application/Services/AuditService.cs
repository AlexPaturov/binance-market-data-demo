using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Worker.Workers;

namespace BinanceDataCollector.Application.Services;

public class AuditService : IAuditService
{
    public IEnumerable<DataGap> FindTradeIdGaps(IEnumerable<long> tradeIds)
    {
        long? previousId = null;
        foreach (var currentId in tradeIds)
        {
            if (previousId.HasValue && currentId > previousId.Value + 1)
            {
                yield return new DataGap(previousId.Value, currentId);
            }
            previousId = currentId;
        }
    }
}
