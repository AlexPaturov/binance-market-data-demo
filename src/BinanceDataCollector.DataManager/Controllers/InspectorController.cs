using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.DataManager.Models;
using BinanceDataCollector.DataManager.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers;

[Authorize(Policy = DataManagerAuthorizationPolicies.Viewer)]
public class InspectorController : Controller
{
    private readonly IArchiveService _archiveService;
    private const int PageSize = 100; // Определяем размер страницы

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
    public async Task<IActionResult> Inspect(string fileName, int p = 1)
    {
        var (symbol, date) = ArchiveFileNameParser.Parse(fileName);

        var model = new ArchiveInspectorViewModel
        {
            ArchivedFiles = await _archiveService.GetArchivedFilesAsync(),
            SelectedArchiveName = fileName,
            InspectedContent = await _archiveService.InspectArchiveContentAsync(fileName, p, PageSize),
            ExpectedDate = date
        };
        return View("Index", model); // Переиспользуем тот же View
    }
}
