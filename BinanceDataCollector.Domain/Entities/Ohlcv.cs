using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Представляет одну свечу (OHLCV - Open, High, Low, Close, Volume).
/// Является агрегированными данными из таблицы Trades.
/// </summary>
[Table("Ohlcv_1min")] // Явно указываем имя таблицы для маппинга
public class Ohlcv
{
    /// <summary>
    /// Валютная пара (например, 'BTCUSDT'). Часть составного первичного ключа.
    /// </summary>
    [Key]
    [Column(Order = 1)] // Указываем порядок в составном ключе
    public required string Symbol { get; set; }

    /// <summary>
    /// Unix-время начала свечи в миллисекундах (UTC). Часть составного первичного ключа.
    /// </summary>
    [Key]
    [Column(Order = 2)]
    public long OpenTime { get; set; }

    /// <summary>
    /// Цена открытия (цена первой сделки в интервале).
    /// </summary>
    public decimal OpenPrice { get; set; }

    /// <summary>
    /// Максимальная цена за интервал.
    /// </summary>
    public decimal HighPrice { get; set; }

    /// <summary>
    /// Минимальная цена за интервал.
    /// </summary>
    public decimal LowPrice { get; set; }

    /// <summary>
    /// Цена закрытия (цена последней сделки в интервале).
    /// </summary>
    public decimal ClosePrice { get; set; }

    /// <summary>
    /// Суммарный объем в базовой валюте.
    /// </summary>
    public decimal Volume { get; set; }
}
