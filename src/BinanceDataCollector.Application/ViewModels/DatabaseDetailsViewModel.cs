using BinanceDataCollector.Application.Models;

namespace BinanceDataCollector.Application.ViewModels;

public class DatabaseDetailsViewModel
{
    public List<PostgresConnectionInfo> Connections { get; set; }
    public List<TableSizeInfo> TableSizes { get; set; }
    public string TotalDatabaseSize { get; set; }
}