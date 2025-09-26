using BinanceDataCollector.Infrastructure.Persistence.Csv.Converters;
using CsvHelper.Configuration.Attributes;

namespace BinanceDataCollector.Infrastructure.Persistence.Csv;

/// <summary>
/// Техническая модель для чтения CSV файлов Binance.
/// </summary>
public class BinanceCsvTradeRecord
{
    // Структура CSV: id,price,qty,quoteQty,time,isBuyerMaker,isBestMatch
    // Источник: https://www.binance.com/en/landing/data

    /// <summary>
    /// Колонка 0: Уникальный ID сделки.
    /// </summary>
    [Index(0)]
    [TypeConverter(typeof(SafeLongConverter))]
    public long Id { get; set; }

    /// <summary>
    /// Колонка 1: Цена сделки.
    /// </summary>
    [Index(1)]
    public decimal Price { get; set; }

    /// <summary>
    /// Колонка 2: Объем в базовой валюте (например, BTC для BTCUSDT).
    /// </summary>
    [Index(2)]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Колонка 3: Объем в котируемой валюте (например, USDT для BTCUSDT).
    /// </summary>
    [Index(3)]
    public decimal QuoteQuantity { get; set; }

    /// <summary>
    /// Колонка 4: Unix Timestamp в миллисекундах (UTC).
    /// ВНИМАНИЕ: Если значение больше 13 цифр, возможно это микросекунды или наносекунды.
    /// </summary>
    [Index(4)]
    [TypeConverter(typeof(SafeLongConverter))]
    public long Time { get; set; }

    /// <summary>
    /// Колонка 5: Был ли покупатель мейкером.
    /// </summary>
    [Index(5)]
    public bool IsBuyerMaker { get; set; }

    /// <summary>
    /// Колонка 6: Была ли сделка по лучшей цене.
    /// </summary>
    [Index(6)]
    [Optional]
    public bool? IsBestMatch { get; set; }
}
