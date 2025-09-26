using System.Text.RegularExpressions;

namespace BinanceDataCollector.Application.Common;

public class ArchiveFileNameParser
{
    // Паттерн: 
    // ^(?<symbol>.+?) - от начала строки захватываем любые символы (не жадно) в группу "symbol"
    // -trades-        - дословно ищем "-trades-"
    // (?<date>\d{4}-\d{2}-\d{2}) - захватываем дату в формате YYYY-MM-DD в группу "date"
    // .zip$           - файл должен заканчиваться на .zip

    // Используем скомпилированный Regex для максимальной производительности
    private static readonly Regex TradeArchiveRegex = new(@"^(?<symbol>.+?)-trades-(?<date>\d{4}-\d{2}-\d{2})\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Парсит имя файла архива сделок и извлекает из него символ и дату.
    /// </summary>
    /// <param name="fileName">Имя файла, например, "BTCUSDT-trades-2025-09-20.zip".</param>
    /// <returns>Кортеж (string Symbol, DateOnly Date). В случае неудачи возвращает ("UNKNOWN", DateOnly.MinValue).</returns>
    public static (string Symbol, DateOnly Date) Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return ("UNKNOWN", DateOnly.MinValue);
        }

        var match = TradeArchiveRegex.Match(fileName);

        if (match.Success && DateOnly.TryParse(match.Groups["date"].Value, out var date))
        {
            return (match.Groups["symbol"].Value.ToUpper(), date); // Приводим символ к верхнему регистру для единообразия
        }

        return ("UNKNOWN", DateOnly.MinValue);
    }
}
