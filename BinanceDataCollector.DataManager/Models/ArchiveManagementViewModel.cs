using BinanceDataCollector.Application.Archives.Models;

namespace BinanceDataCollector.DataManager.Models;

public class ArchiveManagementViewModel
{
    public List<string> ActiveSymbols { get; set; } // Это у тебя уже есть
    public List<ArchivedFileInfo> ArchivedFiles { get; set; }
}
