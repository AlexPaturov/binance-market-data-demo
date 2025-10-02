using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Models;
using BinanceDataCollector.Worker.Workers.Archives;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

public class ArchiveController : Controller
{
    private readonly ITrackedSymbolRepository _symbolRepo;
    private readonly ILogger<ArchiveController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IArchiveService _archiveService;

    public ArchiveController(
        ITrackedSymbolRepository symbolRepo, 
        ILogger<ArchiveController> logger,
        IBackgroundJobClient backgroundJobClient,
        IArchiveService archiveService)
    {
        _symbolRepo = symbolRepo;
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
        _archiveService = archiveService;
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadArchives([FromBody] DownloadArchivesRequest request)
    {
        _logger.LogInformation("Получен запрос на скачивание архивов для {Symbol} с {Start} по {End}",
            request.Symbol, request.StartDate.ToShortDateString(), request.EndDate.ToShortDateString());

        // 1. Генерируем список дат в диапазоне
        var datesToDownload = new List<DateOnly>();
        for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
        {
            datesToDownload.Add(date);
        }

        if (!datesToDownload.Any())
        {
            return BadRequest("Некорректный диапазон дат.");
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
            return BadRequest("Не выбран символ или опция 'Скачать для всех'.");
        }


        int totalJobs = 0;
        foreach (var symbol in symbolsToProcess)
        {
            foreach (var date in datesToDownload)
            {
                _backgroundJobClient.Enqueue<ArchiveDownloaderWorker>(
                    worker => worker.DownloadArchiveAsync(request.RequestId, request.ConnectionId, symbol, date)
                );
                totalJobs++;
            }
        }

        return Ok(new { Message = $"Запланировано скачивание {totalJobs} архивов. Следите за логом." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ProcessArchives([FromBody] ProcessArchivesRequest request)
    {
        if (request?.FileNames == null || !request.FileNames.Any())
        {
            return BadRequest("Не выбраны файлы для обработки.");
        }

        foreach (var fileName in request.FileNames)
        {
            // Ставим задачу в очередь для КАЖДОГО выбранного файла
            // Здесь мы вызываем ArchiveProcessingWorker, который нам еще предстоит создать
            _backgroundJobClient.Enqueue<ArchiveUnpackerWorker>(
                worker => worker.UnpackArchiveAsync(fileName, request.ConnectionId)
            );
        }

        // Возвращаем успешный ответ, который JavaScript сможет обработать
        return Ok(new { Message = $"Запланирована обработка {request.FileNames.Count} архивов. Следите за логом." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteArchives([FromBody] DeleteArchivesRequest request)
    {
        if (request?.FileNames == null || !request.FileNames.Any())
        {
            return BadRequest("Не выбраны файлы для удаления.");
        }

        foreach (var fileName in request.FileNames)
        {
            // Ставим задачу в очередь для нового воркера ArchiveDeletionWorker
            _backgroundJobClient.Enqueue<ArchiveDeletionWorker>(
                worker => worker.DeleteFileAsync(fileName)
            );
        }

        return Ok(new { Message = $"Запланировано удаление {request.FileNames.Count} архивов." });
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
