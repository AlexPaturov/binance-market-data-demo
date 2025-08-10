using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Представляет отслеживаемую валютную пару в базе данных.
/// </summary>
[Table("TrackedSymbols")] // Явно указываем имя таблицы для маппинга
public class TrackedSymbol
{
    /// <summary>
    /// Название пары, например 'BTCUSDT'. Является первичным ключом.
    /// </summary>
    [Key] // Указываем, что это первичный ключ (полезно для других инструментов и для ясности)
    public required string Symbol { get; set; }

    /// <summary>
    /// Флаг: отслеживаем ли мы эту пару в реальном времени.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Дата и время (UTC), когда пара была впервые добавлена в список.
    /// </summary>
    public DateTime DateAdded { get; set; }

    /// <summary>
    /// Дата и время (UTC) последнего сканирования, когда пара была найдена в ТОПе.
    /// Может быть null, если пара новая или давно не сканировалась.
    /// </summary>
    public DateTime? LastScanned { get; set; }
}
