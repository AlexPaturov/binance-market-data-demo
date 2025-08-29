using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// для работы со свечами
/// </summary>
public interface IOhlcvRepository
{
    /// <summary>
    /// Запрос данных для прогрева 
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="startTime"></param>
    /// <param name="warmupPeriod"></param>
    /// <returns></returns>
    Task<IEnumerable<Ohlcv>> GetKlinesWithWarmupAsync(string symbol, long startTime, int warmupPeriod); // на большом пероде 2-4 года будет миллионы записей в запросе -> оптимизация

    Task<IEnumerable<Ohlcv>> GetAllBySymbolAsync(string symbol);
}
