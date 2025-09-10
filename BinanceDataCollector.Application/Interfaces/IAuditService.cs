using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Координирует работу репозиториев и выполняет бизнес-логику, которая не является прямым запросом к БД
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Анализирует последовательность TradeId и находит все пропуски.
    /// </summary>
    IEnumerable<DataGap> FindTradeIdGaps(IEnumerable<long> tradeIds);
}