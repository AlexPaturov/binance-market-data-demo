namespace BinanceDataCollector.DataManager.Models;

public class ChartViewModel
{
    public List<string> ActiveSymbols { get; set; } = new();
    public List<string> Timeframes { get; set; } = new();
    public int DefaultLimit { get; set; }
}
