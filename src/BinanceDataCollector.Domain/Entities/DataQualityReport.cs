namespace BinanceDataCollector.Domain.Entities;

public class DataQualityReport
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public DateTime PeriodMonth { get; set; }
    public long TradeCount { get; set; }
    public int GapCount { get; set; }
    public int InvalidPriceCount { get; set; }
    public int OutlierCount { get; set; }
    public required string Status { get; set; }
    public DateTime CheckedAt { get; set; }
}
