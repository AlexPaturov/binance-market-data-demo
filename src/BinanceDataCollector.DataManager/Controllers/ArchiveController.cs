using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Models;
using BinanceDataCollector.DataManager.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

[Authorize(Policy = DataManagerAuthorizationPolicies.Viewer)]
public class ArchiveController : Controller
{
    private readonly ITrackedSymbolRepository _symbolRepo;
    private readonly ILogger<ArchiveController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IArchiveService _archiveService;
    private readonly ITradeRepository _tradeRepo;

    public ArchiveController(
        ITrackedSymbolRepository symbolRepo,
        ILogger<ArchiveController> logger,
        IBackgroundJobClient backgroundJobClient,
        IRecurringJobManager recurringJobManager,
        IArchiveService archiveService,
        ITradeRepository tradeRepo)
    {
        _symbolRepo = symbolRepo;
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _recurringJobManager = recurringJobManager;
        _archiveService = archiveService;
        _tradeRepo = tradeRepo;
    }

    /// <summary>
    /// Отображает главную страницу управления архивами.
    /// </summary>
    public async Task<IActionResult> Index()
    {
        var model = new ArchiveManagementViewModel
        {
            ActiveSymbols = (List<string>)await _symbolRepo.GetActiveSymbolsAsync(),
            ArchivedFiles = await _archiveService.GetArchivedFilesAsync() // <-- Получаем список файлов
        };

        return View(model);
    }

    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadArchives([FromBody] DownloadArchivesRequest request)
    {
        _logger.LogInformation("Download request received for {Symbol} from {Start} to {End}",
            request.Symbol, request.StartDate.ToShortDateString(), request.EndDate.ToShortDateString());

        // 1. Генерируем список дат в диапазоне
        var datesToDownload = new List<DateOnly>();
        for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
        {
            datesToDownload.Add(date);
        }

        if (!datesToDownload.Any())
        {
            return BadRequest("Invalid date range.");
        }

        List<string> symbolsToProcess;

        if (request.DownloadAll)
        {
            // Если стоит флаг "скачать для всех", получаем список всех активных символов
            symbolsToProcess = await _symbolRepo.GetActiveSymbolsAsync() as List<string>;

        }
        else if (!string.IsNullOrWhiteSpace(request.Symbol))
        {
            // Иначе, берем один символ из запроса
            symbolsToProcess = new List<string> { request.Symbol };
        }
        else
        {
            return BadRequest("No symbol selected and 'Download all' is not checked.");
        }


        // Отсеиваем даты в месяцах ниже границы ретенции: партиции под них нет, вставка
        // провалится — качать гигабайты бессмысленно. Оператору сообщаем, что пропущено.
        var floorMs = await _tradeRepo.GetRetentionFloorMsAsync();
        var allowedDates = datesToDownload.Where(d => !RetentionFloor.IsMonthBelowFloor(d, floorMs)).ToList();
        var skippedDates = datesToDownload.Count - allowedDates.Count;

        if (allowedDates.Count == 0)
        {
            return Ok(new { Message = $"Все {skippedDates} дат ниже границы ретенции — ничего не поставлено в очередь." });
        }

        int totalJobs = 0;
        foreach (var symbol in symbolsToProcess)
        {
            foreach (var date in allowedDates)
            {
                _backgroundJobClient.Enqueue<IArchiveDownloaderWorker>(
                    worker => worker.DownloadArchiveAsync(request.RequestId, request.ConnectionId, symbol, date, JobCancellationToken.Null)
                );
                totalJobs++;
            }
        }

        var message = $"{totalJobs} archive download jobs queued.";
        if (skippedDates > 0)
            message += $" {skippedDates} дат ниже границы ретенции пропущено.";
        return Ok(new { Message = message });
    }

    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ProcessArchives([FromBody] ProcessArchivesRequest request)
    {
        if (request?.FileNames == null || !request.FileNames.Any())
        {
            return BadRequest("No files selected for processing.");
        }

        foreach (var fileName in request.FileNames)
        {
            // Ставим задачу в очередь для КАЖДОГО выбранного файла
            _backgroundJobClient.Enqueue<IArchiveUnpackerWorker>(
                worker => worker.UnpackArchiveAsync(fileName, request.ConnectionId)
            );
        }

        // Возвращаем успешный ответ, который JavaScript сможет обработать
        return Ok(new { Message = $"Processing of {request.FileNames.Count} archives scheduled." });
    }

    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult TriggerSymbolUpdate()
    {
        _recurringJobManager.Trigger("update-symbols");
        _logger.LogInformation("Manual symbol update job queued.");
        return Ok(new { Message = "Обновление символов поставлено в очередь." });
    }

    [Authorize(Policy = DataManagerAuthorizationPolicies.Operator)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteArchives([FromBody] DeleteArchivesRequest request)
    {
        if (request?.FileNames == null || !request.FileNames.Any())
        {
            return BadRequest("No files selected for deletion.");
        }

        foreach (var fileName in request.FileNames)
        {
            // Ставим задачу в очередь для нового воркера ArchiveDeletionWorker
            _backgroundJobClient.Enqueue<IArchiveDeletionWorker>(
                worker => worker.DeleteFileAsync(fileName)
            );
        }

        return Ok(new { Message = $"Deletion of {request.FileNames.Count} archives scheduled." });
    }


}

#region Классы для приема данных от AJAX-запроса
public class ProcessArchivesRequest
{
    public List<string> FileNames { get; set; }
    public string ConnectionId { get; set; }
}

public class DeleteArchivesRequest
{
    public List<string> FileNames { get; set; }
}
#endregion
