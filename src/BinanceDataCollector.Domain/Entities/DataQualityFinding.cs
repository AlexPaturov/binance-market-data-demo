namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Результат одной проверки качества данных за конкретный период.
/// Пишется вручную запускаемыми проверками со страницы /DataQuality.
/// </summary>
public class DataQualityFinding
{
    public long Id { get; set; }

    /// <summary>Группа проверок: trades | ohlcv | features | pipeline.</summary>
    public required string CheckGroup { get; set; }

    /// <summary>Конкретная проверка внутри группы (см. DataQualityChecks).</summary>
    public required string CheckType { get; set; }

    /// <summary>Символ, к которому относится находка. NULL — проверка не привязана к символу.</summary>
    public string? Symbol { get; set; }

    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }

    /// <summary>ok | warning | error.</summary>
    public required string Severity { get; set; }

    /// <summary>Сколько записей нарушают проверку.</summary>
    public long Count { get; set; }

    /// <summary>Подробности в JSON: примеры нарушений, границы, значения. Зависит от CheckType.</summary>
    public string? Details { get; set; }

    public DateTime CheckedAt { get; set; }
}
