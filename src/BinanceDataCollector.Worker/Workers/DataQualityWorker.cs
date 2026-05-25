using BinanceDataCollector.Application.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Layer 1 data integrity checks on raw Trades data.
/// CheckUncheckedMonthsAsync — registered as Cron.Never(), trigger from Hangfire Dashboard.
///   Finds all months that have trade data but no quality report yet, checks each.
/// CheckMonthAsync(year, month) — one-off check for a specific month (enqueue manually).
/// </summary>
[Queue("default")]
[DisableConcurrentExecution(30 * 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class DataQualityWorker
{
    private readonly IDataQualityRepository _qualityRepository;
    private readonly ITrackedSymbolRepository _symbolRepository;
    private readonly ILogger<DataQualityWorker> _logger;

    public DataQualityWorker(
        IDataQualityRepository qualityRepository,
        ITrackedSymbolRepository symbolRepository,
        ILogger<DataQualityWorker> logger)
    {
        _qualityRepository = qualityRepository;
        _symbolRepository = symbolRepository;
        _logger = logger;
    }

    public async Task CheckUncheckedMonthsAsync()
    {
        var uncheckedMonths = await _qualityRepository.GetUncheckedMonthsAsync();
        var months = uncheckedMonths.ToList();

        if (!months.Any())
        {
            _logger.LogInformation("Data quality: no unchecked months found.");
            return;
        }

        _logger.LogInformation("Data quality: found {Count} unchecked month(s): {Months}",
            months.Count, string.Join(", ", months.Select(m => m.ToString("yyyy-MM"))));

        foreach (var month in months)
            await CheckMonthAsync(month.Year, month.Month);
    }

    public async Task CheckMonthAsync(int year, int month)
    {
        _logger.LogInformation("Data quality check started for {Year}-{Month:D2}", year, month);

        var symbols = await _symbolRepository.GetActiveSymbolsAsync();
        var symbolList = symbols.ToList();

        var errors = 0;
        var warnings = 0;

        foreach (var symbol in symbolList)
        {
            var report = await _qualityRepository.CheckSymbolMonthAsync(symbol, year, month);
            await _qualityRepository.UpsertReportAsync(report);

            if (report.Status == "error")
            {
                errors++;
                _logger.LogError(
                    "DQ [{Symbol} {Year}-{Month:D2}] ERROR — trades:{Trades} gaps:{Gaps} invalidPrice:{Invalid} outliers:{Outliers}",
                    symbol, year, month,
                    report.TradeCount, report.GapCount, report.InvalidPriceCount, report.OutlierCount);
            }
            else if (report.Status == "warning")
            {
                warnings++;
                _logger.LogWarning(
                    "DQ [{Symbol} {Year}-{Month:D2}] WARNING — trades:{Trades} gaps:{Gaps} outliers:{Outliers}",
                    symbol, year, month,
                    report.TradeCount, report.GapCount, report.OutlierCount);
            }
            else
            {
                _logger.LogInformation(
                    "DQ [{Symbol} {Year}-{Month:D2}] OK — trades:{Trades}",
                    symbol, year, month, report.TradeCount);
            }
        }

        _logger.LogInformation(
            "Data quality check done for {Year}-{Month:D2}. Symbols:{Total} Errors:{Errors} Warnings:{Warnings}",
            year, month, symbolList.Count, errors, warnings);
    }
}
