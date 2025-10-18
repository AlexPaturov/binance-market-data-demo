using BinanceDataCollector.Application.Archives.Models;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.DataManager.Models;

public class ArchiveInspectorViewModel
{
    // Список ZIP-файлов для верхней таблицы
    public List<ArchivedFileInfo> ArchivedFiles { get; set; } = new();

    // Имя файла, который мы сейчас просматриваем
    public string? SelectedArchiveName { get; set; }

    // Содержимое выбранного архива для нижней таблицы
    public InspectArchiveContentResult? InspectedContent { get; set; }

    public DateOnly ExpectedDate { get; set; }
}
