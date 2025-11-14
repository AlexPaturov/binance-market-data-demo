using BinanceDataCollector.Application.Archives.Models;

namespace BinanceDataCollector.DataManager.Models;

public class ArchiveManagementViewModel
{
    public List<string> ActiveSymbols { get; set; } 
    public List<ArchivedFileInfo> ArchivedFiles { get; set; }
}
