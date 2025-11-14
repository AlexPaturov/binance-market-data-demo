namespace BinanceDataCollector.Domain.Entities;

// Атрибут для явного указания имени таблицы (хорошая практика)
[System.ComponentModel.DataAnnotations.Schema.Table("Orders")]
public class Order
{
    public long OrderId { get; set; }

    public string? ClientOrderId { get; set; }

    public string Symbol { get; set; } = null!;

    public string Side { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? TimeInForce { get; set; }

    public decimal? Price { get; set; }

    public decimal? OrigQty { get; set; }

    public decimal? ExecutedQty { get; set; }

    public decimal? CummulativeQuoteQty { get; set; }

    public decimal? StopPrice { get; set; }

    public decimal? IcebergQty { get; set; }

    public bool? IsWorking { get; set; } = true;

    public long CreateTime { get; set; }
}
