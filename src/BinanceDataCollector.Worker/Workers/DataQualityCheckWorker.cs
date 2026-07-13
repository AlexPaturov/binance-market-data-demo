using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Проверки качества данных. Запускаются ТОЛЬКО вручную — кнопкой на странице
/// /DataQuality в DataManager. В расписании не регистрируется и сама не стартует.
///
/// Hangfire нужен здесь как исполнитель, а не как планировщик: проверка по "Trades"
/// сканирует сотни ГБ и не укладывается в таймаут HTTP-запроса.
/// </summary>
[Queue("default")]
[DisableConcurrentExecution(timeoutInSeconds: 2 * 60 * 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class DataQualityCheckWorker
{
    private readonly IDataQualityRepository _repository;
    private readonly IStatusNotifier _notifier;
    private readonly ILogger<DataQualityCheckWorker> _logger;

    public DataQualityCheckWorker(
        IDataQualityRepository repository,
        IStatusNotifier notifier,
        ILogger<DataQualityCheckWorker> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
    }

    /// <param name="groups">Группы проверок: trades | ohlcv | features | pipeline.</param>
    /// <param name="symbol">Символ или null — проверять все.</param>
    /// <param name="from">Начало периода (UTC).</param>
    /// <param name="to">Конец периода (UTC). Диапазон ограничен 31 днём.</param>
    /// <param name="connectionId">SignalR-соединение, куда слать прогресс.</param>
    public async Task RunChecksAsync(
        string[] groups, string? symbol, DateTime from, DateTime to, string connectionId)
    {
        var scope = symbol ?? "все символы";
        _logger.LogInformation(
            "Data quality check started. Groups: {Groups}, symbol: {Symbol}, period: {From:yyyy-MM-dd} — {To:yyyy-MM-dd}",
            string.Join(", ", groups), scope, from, to);

        await _notifier.SendStatusUpdateAsync(connectionId,
            $"Проверка запущена: {string.Join(", ", groups)} | {scope} | {from:yyyy-MM-dd} — {to:yyyy-MM-dd}");

        var allFindings = new List<DataQualityFinding>();

        foreach (var group in groups)
        {
            if (!DataQualityChecks.IsKnownGroup(group))
            {
                _logger.LogWarning("Unknown check group: {Group}. Skipped.", group);
                await _notifier.SendStatusUpdateAsync(connectionId, $"Неизвестная группа проверок: {group}. Пропущена.");
                continue;
            }

            await _notifier.SendStatusUpdateAsync(connectionId, $"Выполняю группу «{group}»...");

            try
            {
                var findings = group switch
                {
                    DataQualityChecks.GroupTrades   => await _repository.RunTradesChecksAsync(symbol, from, to),
                    DataQualityChecks.GroupOhlcv    => await _repository.RunOhlcvChecksAsync(symbol, from, to),
                    DataQualityChecks.GroupFeatures => await _repository.RunFeaturesChecksAsync(symbol, from, to),
                    DataQualityChecks.GroupPipeline => await _repository.RunPipelineChecksAsync(),
                    _ => Array.Empty<DataQualityFinding>()
                };

                allFindings.AddRange(findings);

                var problems = findings.Count(f => f.Severity != DataQualityChecks.SeverityOk);
                await _notifier.SendStatusUpdateAsync(connectionId,
                    problems > 0
                        ? $"Группа «{group}»: найдено проблем — {problems} из {findings.Count} проверок."
                        : $"Группа «{group}»: без замечаний ({findings.Count} проверок).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Check group {Group} failed", group);
                await _notifier.SendStatusUpdateAsync(connectionId,
                    $"<b style='color:red;'>Ошибка</b> в группе «{group}»: {ex.Message}");
                throw;
            }
        }

        await _repository.SaveFindingsAsync(allFindings);

        var errors = allFindings.Count(f => f.Severity == DataQualityChecks.SeverityError);
        var warnings = allFindings.Count(f => f.Severity == DataQualityChecks.SeverityWarning);

        _logger.LogInformation(
            "Data quality check done. Checks: {Total}, errors: {Errors}, warnings: {Warnings}",
            allFindings.Count, errors, warnings);

        await _notifier.SendStatusUpdateAsync(connectionId,
            $"Проверка завершена. Всего: {allFindings.Count}, ошибок: {errors}, предупреждений: {warnings}. Обновите таблицу результатов.");
    }

    /// <summary>
    /// Месячный отчёт по сырым тикам ("DataQualityReports") — тоже только вручную,
    /// кнопкой. Раньше висел рекуррентной джобой с Cron.Never().
    /// </summary>
    public async Task CheckSymbolMonthAsync(string symbol, int year, int month, string connectionId)
    {
        _logger.LogInformation("Monthly report started for {Symbol} {Year}-{Month:D2}", symbol, year, month);
        await _notifier.SendStatusUpdateAsync(connectionId,
            $"Месячный отчёт: {symbol}, {year}-{month:D2}...");

        var report = await _repository.CheckSymbolMonthAsync(symbol, year, month);
        await _repository.UpsertReportAsync(report);

        _logger.LogInformation(
            "Monthly report done [{Symbol} {Year}-{Month:D2}] {Status} — trades:{Trades} gaps:{Gaps} invalid:{Invalid} outliers:{Outliers}",
            symbol, year, month, report.Status,
            report.TradeCount, report.GapCount, report.InvalidPriceCount, report.OutlierCount);

        await _notifier.SendStatusUpdateAsync(connectionId,
            $"Отчёт готов [{symbol} {year}-{month:D2}] — {report.Status}: сделок {report.TradeCount}, " +
            $"разрывов {report.GapCount}, невалидных {report.InvalidPriceCount}, выбросов {report.OutlierCount}.");
    }
}
