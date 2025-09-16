using BinanceDataCollector.Domain.Entities;
using System.Collections.Concurrent;

namespace BinanceDataCollector.Worker.Common;

public class GapProcessingTracker
{
    // Используем ConcurrentDictionary для потокобезопасности.
    // Ключ - это строка вида "BTCUSDT:1000-2000"
    // Значение - неважно, просто флаг.
    private readonly ConcurrentDictionary<string, byte> _currentlyProcessing = new();

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
    /// Помечает дыру как "обработка завершена".
    /// </summary>
    public void MarkAsCompleted(string symbol, DataGap gap)
    {
        _currentlyProcessing.TryRemove(GetKey(symbol, gap), out _);
    }
}
