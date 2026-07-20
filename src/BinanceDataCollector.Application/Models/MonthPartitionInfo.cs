namespace BinanceDataCollector.Application.Models;

/// <summary>
/// Помесячная сводка по партиции Trades для панели БД: где лежит месяц
/// (горячий SSD / холодный HDD) и запечатан ли он (критерий закрытого месяца).
/// </summary>
public class MonthPartitionInfo
{
    public string Month { get; set; }          // "2026-06"
    public string Size { get; set; }           // pg_size_pretty, напр. "128 GB"
    public bool OnCold { get; set; }           // true — партиция на tablespace cold (внешний HDD)
    public bool Sealed { get; set; }           // есть строка в MonthSeal
    public DateTimeOffset? SealedAt { get; set; }
    public bool IsCurrentMonth { get; set; }   // текущий календарный месяц — «открыт» штатно
    public string? ReasonCode { get; set; }    // null — данные месяца готовы; иначе причина «не запечатан»
    public string State { get; set; }          // готовая подпись для UI (собирается в сервисе)
}
