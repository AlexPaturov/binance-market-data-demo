using BinanceDataCollector.Application.Archives;
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
        // Получаем список символов, чтобы передать его в выпадающий список на View
        //var symbols = await _symbolRepo.GetActiveSymbolsAsync();
        // Передаем список символов во View. ViewData - это простой способ передать небольшие данные из контроллера в представление.
        //ViewData["Symbols"] = symbols.ToList();

        var model = new ArchiveManagementViewModel
        {
            ActiveSymbols = (List<string>)await _symbolRepo.GetActiveSymbolsAsync(),
            ArchivedFiles = await _archiveService.GetArchivedFilesAsync() // <-- Получаем список файлов
        };

        return View(model);
    }

    [HttpPost] // Реагирует на POST-запросы
    [Route("api/archive/download")] // Устанавливаем ему понятный URL
    public IActionResult StartDownload([FromForm] string symbol, [FromForm] DateTime startDate, [FromForm] DateTime endDate)
    {
        _logger.LogInformation("Получен запрос на скачивание архивов для {Symbol} с {Start} по {End}",
            symbol, startDate.ToShortDateString(), endDate.ToShortDateString());

        // 1. Генерируем список дат в диапазоне
        var datesToDownload = new List<DateOnly>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            datesToDownload.Add(DateOnly.FromDateTime(date));
        }

        // 2. Ставим задачи в очередь Hangfire
        foreach (var date in datesToDownload)
        {
            _logger.LogDebug("Ставим в очередь задачу на скачивание для {Symbol} за {Date}", symbol, date);
            _backgroundJobClient.Enqueue<ArchiveDownloaderWorker>(
                worker => worker.DownloadArchiveAsync(symbol, date)
             );
        }

        // 3. Возвращаем успешный ответ
        return Ok(new { Message = $"Запланировано скачивание {datesToDownload.Count} архивов. Следите за логом." });
    }

    [HttpPost] // Этот метод будет вызываться AJAX-запросом
    [ValidateAntiForgeryToken] // Защита от CSRF-атак
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
                worker => worker.UnpackArchiveAsync(fileName)
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

// Простой класс для приема данных от AJAX-запроса
public class ProcessArchivesRequest
{
    public List<string> FileNames { get; set; }
}

public class DeleteArchivesRequest
{
    public List<string> FileNames { get; set; }
}
