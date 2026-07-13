namespace BinanceDataCollector.Application.Common;

/// <summary>
/// Таймфреймы графика. Свечи собираются на лету из минутных (`Ohlcv_1min`) —
/// отдельных таблиц под каждый интервал нет.
/// </summary>
public static class ChartTimeframes
{
    public const string M15 = "15m";
    public const string H1  = "1h";
    public const string H4  = "4h";
    public const string D1  = "1d";
    public const string W1  = "1w";

    public static readonly IReadOnlyList<string> All = new[] { M15, H1, H4, D1, W1 };

    /// <summary>Длительность бара в миллисекундах.</summary>
    public static long BucketMs(string timeframe) => timeframe switch
    {
        M15 => 900_000L,
        H1  => 3_600_000L,
        H4  => 14_400_000L,
        D1  => 86_400_000L,
        W1  => 604_800_000L,
        _   => throw new ArgumentException($"Неизвестный таймфрейм: {timeframe}")
    };

    /// <summary>
    /// Сдвиг сетки баров. Unix-эпоха начинается в четверг, поэтому недельные свечи,
    /// выровненные по эпохе, открывались бы в четверг. Биржи открывают неделю
    /// в понедельник — сдвигаем сетку на 4 дня. Остальные интервалы делят сутки
    /// нацело и выравниваются по UTC-полуночи сами.
    /// </summary>
    public static long AlignmentOffsetMs(string timeframe) => timeframe switch
    {
        W1 => 345_600_000L,   // 4 дня: четверг → понедельник
        _  => 0L
    };

    public static bool IsKnown(string timeframe) => All.Contains(timeframe);

    /// <summary>Сколько баров отдаём по умолчанию и максимум за один запрос.</summary>
    public const int DefaultLimit = 500;
    public const int MaxLimit = 1500;
}
