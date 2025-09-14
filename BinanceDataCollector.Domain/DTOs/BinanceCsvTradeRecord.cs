using CsvHelper.Configuration.Attributes;

namespace BinanceDataCollector.Domain.DTOs;

/// <summary>
/// Представляет одну строку из CSV-файла с сырыми сделками от Binance.
/// Порядок свойств и атрибуты [Index] должны ТОЧНО соответствовать
/// структуре CSV-файла.
/// </summary>
public class BinanceCsvTradeRecord
{
    // Структура CSV: id,price,qty,quoteQty,time,isBuyerMaker,isBestMatch
    // Источник: https://www.binance.com/en/landing/data

    /// <summary>
    /// Колонка 0: Уникальный ID сделки.
    /// </summary>
    [Index(0)]
    public long Id { get; set; }

    /// <summary>
    /// Колонка 1: Цена сделки.
    /// </summary>
    [Index(1)]
    public decimal Price { get; set; }

    /// <summary>
    /// Колонка 2: Объем в базовой валюте (например, BTC для BTCUSDT).
    /// ВАЖНО: В документации Binance это 'qty', в API - 'baseQuantity'.
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
    /// </summary>
    [Index(4)]
    public long Time { get; set; }

    /// <summary>
    /// Колонка 5: Был ли покупатель мейкером.
    /// </summary>
    [Index(5)]
    public bool IsBuyerMaker { get; set; }

    /// <summary>
    /// Колонка 6: Была ли сделка по лучшей цене.
    /// В старых архивах этого поля может не быть, CsvHelper обработает это.
    /// </summary>
    [Index(6)]
    [Optional] // Делаем поле необязательным
    public bool? IsBestMatch { get; set; }
}
