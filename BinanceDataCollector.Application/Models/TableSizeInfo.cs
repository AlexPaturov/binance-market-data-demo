namespace BinanceDataCollector.Application.Models;

public class TableSizeInfo
{
    public string TableName { get; set; }
    public string TableSize { get; set; } 
    public string IndexSize { get; set; } 
    public string TotalSize { get; set; } // e.g., "1.8 GB"
}