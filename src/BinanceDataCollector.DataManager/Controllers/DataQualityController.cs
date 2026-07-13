using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common.Auth;
using BinanceDataCollector.DataManager.Models;
using BinanceDataCollector.Worker.Workers;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

[Authorize(Policy = DataManagerAuthorizationPolicies.Viewer)]
public class DataQualityController : Controller
{
    private readonly IDataQualityRepository _qualityRepo;
    private readonly ITrackedSymbolRepository _symbolRepo;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<DataQualityController> _logger;

    public DataQualityController(
        IDataQualityRepository qualityRepo,
        ITrackedSymbolRepository symbolRepo,
        IBackgroundJobClient backgroundJobClient,
        ILogger<DataQualityController> logger)
    {
        _qualityRepo = qualityRepo;
        _symbolRepo = symbolRepo;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string? group = null, string? severity = null, string? symbol = null)
    {
        var model = new DataQualityViewModel
        {
            ActiveSymbols   = (await _symbolRepo.GetActiveSymbolsAsync()).ToList(),
            Findings        = (await _qualityRepo.GetFindingsAsync(group, severity, symbol)).ToList(),
            Reports         = (await _qualityRepo.GetReportsAsync()).ToList(),
            UncheckedMonths = (await _qualityRepo.GetUncheckedMonthsAsync()).ToList(),
            FilterGroup     = group,
            FilterSeverity  = severity,
            FilterSymbol    = symbol,
            MaxRangeDays    = (int)DataQualityChecks.MaxRange.TotalDays
        };

        return View(model);
    }

    /// <summary>
    /// Ставит проверки в очередь Hangfire. Синхронно их выполнять нельзя:
    /// скан по "Trades" длится минуты и не укладывается в таймаут HTTP-запроса.
    /// </summary>
    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RunChecks([FromBody] RunChecksRequest request)
    {
        if (request.Groups.Length == 0)
            return BadRequest("Не выбрано ни одной группы проверок.");

        var unknown = request.Groups.Where(g => !DataQualityChecks.IsKnownGroup(g)).ToList();
        if (unknown.Count > 0)
            return BadRequest($"Неизвестные группы проверок: {string.Join(", ", unknown)}.");

        if (request.To <= request.From)
            return BadRequest("Конец периода должен быть позже начала.");

        if (request.To - request.From > DataQualityChecks.MaxRange)
            return BadRequest(
                $"Диапазон не может превышать {DataQualityChecks.MaxRange.TotalDays:0} дней. " +
                $"Запрошено: {(request.To - request.From).TotalDays:0}.");

        var symbol = string.IsNullOrWhiteSpace(request.Symbol) ? null : request.Symbol;

        _logger.LogInformation(
            "Data quality check requested. Groups: {Groups}, symbol: {Symbol}, {From:yyyy-MM-dd}—{To:yyyy-MM-dd}",
            string.Join(",", request.Groups), symbol ?? "all", request.From, request.To);

        _backgroundJobClient.Enqueue<DataQualityCheckWorker>(worker =>
            worker.RunChecksAsync(request.Groups, symbol, request.From, request.To, request.ConnectionId));

        return Ok(new { Message = $"Проверки поставлены в очередь: {string.Join(", ", request.Groups)}." });
    }

    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RunMonthlyReport([FromBody] RunMonthlyReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest("Не выбран символ.");

        if (request.Month is < 1 or > 12)
            return BadRequest("Некорректный месяц.");

        _logger.LogInformation("Monthly report requested for {Symbol} {Year}-{Month:D2}",
            request.Symbol, request.Year, request.Month);

        _backgroundJobClient.Enqueue<DataQualityCheckWorker>(worker =>
            worker.CheckSymbolMonthAsync(request.Symbol, request.Year, request.Month, request.ConnectionId));

        return Ok(new { Message = $"Отчёт поставлен в очередь: {request.Symbol}, {request.Year}-{request.Month:D2}." });
    }
}
