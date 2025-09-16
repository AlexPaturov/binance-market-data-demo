using BinanceDataCollector.Domain.Entities;
using System.Collections.Concurrent;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Сервис для отслеживания фоновых задач, которые уже находятся в обработке.
/// </summary>
/// <remarks>
/// <para>
/// **Назначение:** Решает проблему "спама" дублирующимися задачами в Hangfire.
/// Такие воркеры, как `QuickAuditorWorker` и `HistoricalAuditorWorker`, могут запускаться
/// по расписанию чаще, чем успевают выполниться порожденные ими задачи по заполнению дыр.
/// Это приводит к тому, что один и тот же пробел в данных обнаруживается многократно
/// и в очередь ставится множество идентичных задач на его "ремонт".
/// </para>
/// <para>
/// **Принцип работы:** Этот сервис работает как легковесный, потокобезопасный "журнал учета работ",
/// хранящийся в памяти.
/// <list type="number">
/// <item>
/// <description>Перед тем как поставить "долгую" задачу в очередь (например, `FillGapWorker`), "планировщик" (`QuickAuditorWorker`)
/// пытается "зарезервировать" ее в этом трекере с помощью методов `TryMark...AsProcessing`.</description>
/// </item>
/// <item>
/// <description>Если резервирование успешно (метод вернул `true`), значит, эта работа еще не ведется, и можно ставить задачу в Hangfire.</description>
/// </item>
/// <item>
/// <description>Если резервирование не удалось (`false`), значит, аналогичная задача уже была поставлена ранее и, возможно, все еще ждет
/// выполнения в очереди Hangfire. "Планировщик" пропускает ее, избегая создания дубликата.</description>
/// </item>
/// <item>
/// <description>Когда "исполнитель" (`FillGapWorker`) завершает свою работу (успешно или с ошибкой), он в блоке `finally`
/// вызывает метод `Mark...AsCompleted`, чтобы "снять резерв" и позволить "планировщику" в будущем снова проверить этот диапазон данных.</description>
/// </item>
/// </list>
/// </para>
/// <para>
/// **Жизненный цикл:** Этот сервис должен быть зарегистрирован в DI-контейнере как **Singleton**,
/// чтобы все воркеры использовали один и тот же экземпляр "журнала учета".
/// </para>
/// </remarks>
public class GapProcessingTracker
{
    // Используем ConcurrentDictionary для обеспечения потокобезопасности.
    // Ключ - это уникальная строка, идентифицирующая "работу" (например, "gap:BTCUSDT:1000-2000").
    // Значение (byte) используется просто как легковесный флаг-заглушка.
    private readonly ConcurrentDictionary<string, byte> _currentlyProcessing = new();

    #region Методы для БЫСТРОГО аудита (по объекту DataGap)

    /// <summary>
    /// Генерирует уникальный ключ для отслеживания обработки конкретной дыры в TradeId.
    /// </summary>
    private string GetKey(string symbol, DataGap gap) => $"{symbol}:{gap.GapStart}-{gap.GapEnd}";

    /// <summary>
    /// Пытается пометить дыру как "в обработке".
    /// </summary>
    /// <returns>True, если дыра еще не обрабатывалась, иначе false.</returns>
    public bool TryMarkAsProcessing(string symbol, DataGap gap)
    {
        return _currentlyProcessing.TryAdd(GetKey(symbol, gap), 1);
    }

    /// <summary>
    /// Помечает дыру, определенную объектом DataGap, как "обработка завершена", снимая блокировку.
    /// </summary>
    public void MarkGapAsCompleted(string symbol, DataGap gap)
    {
        _currentlyProcessing.TryRemove(GetKey(symbol, gap), out _);
    }
    #endregion

    #region Методы для ИСТОРИЧЕСКОГО аудита (по дате архива)

    /// <summary>
    /// Генерирует уникальный ключ для отслеживания обработки архива за конкретный день.
    /// </summary>
    private string GetArchiveKey(string symbol, DateOnly date) => $"archive:{symbol}:{date:yyyy-MM-dd}";

    /// <summary>
    /// Пытается пометить задачу по импорту архива как "в обработке".
    /// </summary>
    /// <returns>True, если задача еще не обрабатывалась и была успешно помечена, иначе false.</returns>
    public bool TryMarkArchiveAsProcessing(string symbol, DateOnly date) => _currentlyProcessing.TryAdd(GetArchiveKey(symbol, date), 1);

    /// <summary>
    /// Помечает задачу по импорту архива как "обработка завершена", снимая блокировку.
    /// </summary>
    public void MarkArchiveAsCompleted(string symbol, DateOnly date) => _currentlyProcessing.TryRemove(GetArchiveKey(symbol, date), out _);
    #endregion
}
