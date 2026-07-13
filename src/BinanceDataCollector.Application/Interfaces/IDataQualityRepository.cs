using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IDataQualityRepository
{
    // --- Месячные отчёты по сырым тикам ("DataQualityReports") ---
    Task<DataQualityReport> CheckSymbolMonthAsync(string symbol, int year, int month);
    Task UpsertReportAsync(DataQualityReport report);
    Task<IEnumerable<DataQualityReport>> GetReportsAsync(string? symbol = null, string? status = null);
    Task<IEnumerable<DateTime>> GetUncheckedMonthsAsync();

    // --- Проверки, запускаемые вручную со страницы /DataQuality ("DataQualityFindings") ---

    /// <summary>Разрывы TradeId, невалидные цены, выбросы, дубликаты, неотслеживаемые символы.</summary>
    Task<IReadOnlyList<DataQualityFinding>> RunTradesChecksAsync(string? symbol, DateTime from, DateTime to);

    /// <summary>Инварианты OHLC, выравнивание OpenTime, пропущенные минуты, нулевой объём при наличии тиков.</summary>
    Task<IReadOnlyList<DataQualityFinding>> RunOhlcvChecksAsync(string? symbol, DateTime from, DateTime to);

    /// <summary>Диапазон RSI, свечи без индикаторов, осиротевшие индикаторы.</summary>
    Task<IReadOnlyList<DataQualityFinding>> RunFeaturesChecksAsync(string? symbol, DateTime from, DateTime to);

    /// <summary>Состояние watermark'ов и аудита. Не зависит от периода — смотрит текущее состояние.</summary>
    Task<IReadOnlyList<DataQualityFinding>> RunPipelineChecksAsync();

    Task SaveFindingsAsync(IEnumerable<DataQualityFinding> findings);

    Task<IEnumerable<DataQualityFinding>> GetFindingsAsync(
        string? checkGroup = null, string? severity = null, string? symbol = null, int limit = 200);
}
