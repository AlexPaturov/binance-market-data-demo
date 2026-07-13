namespace BinanceDataCollector.Application.Common;

/// <summary>
/// Каталог проверок качества данных. Группы соответствуют кнопкам на странице /DataQuality.
/// </summary>
public static class DataQualityChecks
{
    public const string GroupTrades = "trades";
    public const string GroupOhlcv = "ohlcv";
    public const string GroupFeatures = "features";
    public const string GroupPipeline = "pipeline";

    public static readonly IReadOnlyList<string> AllGroups =
        new[] { GroupTrades, GroupOhlcv, GroupFeatures, GroupPipeline };

    public const string SeverityOk = "ok";
    public const string SeverityWarning = "warning";
    public const string SeverityError = "error";

    /// <summary>
    /// Максимальный диапазон одной проверки. Ограничение жёсткое: без него любая
    /// проверка превращается в полный скан истории (сотни ГБ в "Trades").
    /// </summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    public static bool IsKnownGroup(string group) => AllGroups.Contains(group);
}
