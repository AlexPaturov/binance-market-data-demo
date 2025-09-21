using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Worker.Workers;
using Microsoft.AspNetCore.Mvc;
using Hangfire;

namespace BinanceDataCollector.DataManager.Controllers;

public class ArchiveController : Controller
{
    private readonly ITrackedSymbolRepository _symbolRepo;
    private readonly ILogger<ArchiveController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public ArchiveController(
        ITrackedSymbolRepository symbolRepo, 
        ILogger<ArchiveController> logger,
        IBackgroundJobClient backgroundJobClient)
    {
        _symbolRepo = symbolRepo;
        _logger = logger;
        _backgroundJobClient = backgroundJobClient;
    }

    /// <summary>
    /// Отображает главную страницу управления архивами.
    /// </summary>
    public async Task<IActionResult> Index()
    {
        // Получаем список символов, чтобы передать его в выпадающий список на View
        var symbols = await _symbolRepo.GetActiveSymbolsAsync();
        // Передаем список символов во View. ViewData - это простой способ передать небольшие данные из контроллера в представление.
        ViewData["Symbols"] = symbols.ToList();
        return View();
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
            _backgroundJobClient.Enqueue<ArchiveDownloaderWorker>(worker => worker.DownloadArchiveAsync(symbol, date));
        }

        // 3. Возвращаем успешный ответ
        return Ok(new { Message = $"Запланировано скачивание {datesToDownload.Count} архивов. Следите за логом." });
    }
}
