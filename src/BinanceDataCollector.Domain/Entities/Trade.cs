using System.Data;
using System.Numerics;

namespace BinanceDataCollector.Domain.Entities;

[System.ComponentModel.DataAnnotations.Schema.Table("Trades")]
public class Trade
{
    public long TradeId { get; set; }
    public required string Symbol { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuoteQuantity { get; set; }
    public long TradeTime { get; set; }
    public bool IsBuyerMaker { get; set; }
    public bool IsBestMatch { get; set; }
    public long? OrderId { get; set; }
    public decimal? Commission { get; set; }
    public string? CommissionAsset { get; set; }
    public bool IsMyTrade { get; set; }
}
