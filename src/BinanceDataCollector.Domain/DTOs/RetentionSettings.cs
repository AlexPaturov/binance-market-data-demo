namespace BinanceDataCollector.Domain.DTOs;

/// <summary>
/// Ротация данных по размеру диска. См. docs/adr/0007-size-based-retention-and-unified-partitioning.md
///
/// Календарное окно («держим 13 месяцев») непредсказуемо в байтах: реальные месячные
/// партиции различаются в 4.6 раза (32–148 ГБ) в зависимости от активности рынка и числа
/// отслеживаемых пар. Поэтому окно задаётся размером, а не числом месяцев.
/// </summary>
public class RetentionSettings
{
    /// <summary>
    /// Порог, выше которого ротация начинает дропать старейшие месяцы.
    /// Диск 3.6 ТБ: 2.8 ТБ рабочих, 0.8 ТБ резерв под WAL, temp-файлы тяжёлых
    /// оконных запросов в проверках качества, bloat и аномально жирный месяц.
    /// </summary>
    public long MaxPartitionedGigabytes { get; set; } = 2800;

    /// <summary>
    /// Предохранитель от самоуничтожения при ошибочно заниженном пороге:
    /// месяцы свежее этого окна не дропаются никогда, даже под давлением диска.
    /// </summary>
    public int MinMonthsToKeep { get; set; } = 6;

    public long MaxPartitionedBytes => MaxPartitionedGigabytes * 1024L * 1024L * 1024L;
}
