using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.DataManager.Models;

public class DataQualityViewModel
{
    public List<string> ActiveSymbols { get; set; } = new();

    /// <summary>Результаты проверок, запускавшихся вручную.</summary>
    public List<DataQualityFinding> Findings { get; set; } = new();

    /// <summary>Месячные отчёты по сырым тикам.</summary>
    public List<DataQualityReport> Reports { get; set; } = new();

    /// <summary>Месяцы, по которым отчёта ещё нет (есть данные в партициях).</summary>
    public List<DateTime> UncheckedMonths { get; set; } = new();

    // Активные фильтры
    public string? FilterGroup { get; set; }
    public string? FilterSeverity { get; set; }
    public string? FilterSymbol { get; set; }

    /// <summary>Максимальный диапазон одной проверки, дней.</summary>
    public int MaxRangeDays { get; set; }
}

/// <summary>Запрос на запуск проверок со страницы.</summary>
public class RunChecksRequest
{
    public string[] Groups { get; set; } = Array.Empty<string>();
    public string? Symbol { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>Запрос на месячный отчёт по сырым тикам.</summary>
public class RunMonthlyReportRequest
{
    public string Symbol { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
}
