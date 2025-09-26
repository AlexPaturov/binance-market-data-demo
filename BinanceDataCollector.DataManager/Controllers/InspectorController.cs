using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.DataManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

public class InspectorController : Controller
{
    private readonly IArchiveService _archiveService;

    public InspectorController(IArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    // GET: /Inspector или /Inspector/Index
    public async Task<IActionResult> Index()
    {
        var model = new ArchiveInspectorViewModel
        {
            ArchivedFiles = await _archiveService.GetArchivedFilesAsync()
        };
        return View(model);
    }

    // GET: /Inspector/Inspect?fileName=...
    public async Task<IActionResult> Inspect(string fileName)
    {
        var (symbol, date) = ArchiveFileNameParser.Parse(fileName);

        var model = new ArchiveInspectorViewModel
        {
            ArchivedFiles = await _archiveService.GetArchivedFilesAsync(),
            SelectedArchiveName = fileName,
            TradesInArchive = await _archiveService.InspectArchiveContentAsync(fileName),
            ExpectedDate = date
        };
        return View("Index", model); // Переиспользуем тот же View
    }
}
