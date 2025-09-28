using BinanceDataCollector.Domain.Entities;
using System.Diagnostics;

namespace BinanceDataCollector.Infrastructure.Persistence.Csv.Mappers;

public class TradeMapper
{
    public static Trade ToDomainEntity(BinanceCsvTradeRecord csvRecord, string symbol)
    {
        //Console.WriteLine($"ToDomainEntity -> {DateTimeOffset.FromUnixTimeMilliseconds(NormalizeTimestamp(csvRecord.Time)).ToString("yyyy-MM-dd HH:mm:ss.fff")}");
        return new Trade
        {
            TradeId = csvRecord.Id,
            Symbol = symbol,
            Price = csvRecord.Price,
            Quantity = csvRecord.Quantity,
            QuoteQuantity = csvRecord.QuoteQuantity,
            TradeTime = NormalizeTimestamp(csvRecord.Time),
            IsBuyerMaker = csvRecord.IsBuyerMaker,
            IsBestMatch = csvRecord.IsBestMatch ?? false
        };
    }

    /// <summary>
    /// Конвертирует timestamp в DateTime с учетом разных форматов.
    /// </summary>
    private static DateTime ConvertTimestampToDateTime(long timestamp)
    {
        var normalizedMs = NormalizeTimestamp(timestamp);
        return DateTimeOffset.FromUnixTimeMilliseconds(normalizedMs).DateTime;
    }

    private static long NormalizeTimestamp(long timestamp)
    {
        var digits = timestamp.ToString().Length;

        return digits switch
        {
            10 => timestamp * 1000,      // секунды -> миллисекунды
            13 => timestamp,             // уже миллисекунды
            16 => timestamp / 1000,      // микросекунды -> миллисекунды
            19 => timestamp / 1000000,   // наносекунды -> миллисекунды
            _ => timestamp
        };
    }
}

