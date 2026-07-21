namespace BinanceDataCollector.Application.Models;

/// <summary>
/// Результат помесячной сводки для панели БД. Отделяет «запрос успешно вернул пусто»
/// от «запрос упал/не уложился в таймаут»: панель должна показывать эти два случая
/// по-разному, а не сводить оба к «нет партиций».
/// </summary>
public class MonthPartitionsResult
{
    /// <summary>true — запрос отработал (список актуален, пусть и пустой); false — таймаут/ошибка.</summary>
    public bool Available { get; init; }

    public List<MonthPartitionInfo> Months { get; init; } = new();

    public static MonthPartitionsResult Ok(List<MonthPartitionInfo> months) =>
        new() { Available = true, Months = months };

    public static MonthPartitionsResult Unavailable() =>
        new() { Available = false };
}
