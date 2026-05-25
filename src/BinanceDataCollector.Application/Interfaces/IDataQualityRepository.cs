using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IDataQualityRepository
{
    Task<DataQualityReport> CheckSymbolMonthAsync(string symbol, int year, int month);
    Task UpsertReportAsync(DataQualityReport report);
    Task<IEnumerable<DataQualityReport>> GetReportsAsync(string? symbol = null, string? status = null);
    Task<IEnumerable<DateTime>> GetUncheckedMonthsAsync();
}
