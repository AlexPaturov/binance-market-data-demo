namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// для таблицы с индикаторами
/// </summary>
public class FeatureData
{
    public string Symbol { get; set; }
    public long OpenTime { get; set; }
    public decimal? Rsi14 { get; set; }
    public decimal? MacdSignal { get; set; }
    public decimal? MacdHist { get; set; }
    public decimal? Cvd { get; set; }
}
