namespace BinanceDataCollector.Application.Common;

/// <summary>
/// Граница ретенции по размеру диска: месяцы ниже неё дропнуты, и партиция под них
/// не создаётся (<c>sp_ensure_month_partitions</c>). Скачивать архив за такой месяц
/// бессмысленно — вставка всё равно упрётся в отсутствие партиции. Проверяем ДО скачивания.
/// </summary>
public static class RetentionFloor
{
    /// <param name="floorMs">Результат <c>public.fn_retention_floor_ms()</c>; 0 — ретенция не активна.</param>
    /// <returns>true, если месяц <paramref name="date"/> ниже границы (архив качать не нужно).</returns>
    public static bool IsMonthBelowFloor(DateOnly date, long floorMs)
    {
        if (floorMs <= 0) return false;

        // Сравниваем начало месяца даты — так же, как БД (from_ms < floor_ms).
        var monthStartMs = new DateTimeOffset(date.Year, date.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        return monthStartMs < floorMs;
    }
}
