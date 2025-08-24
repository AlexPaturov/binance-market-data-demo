using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAnalysisRepository
{
    Task<IEnumerable<CvdResult>> GetCvdForOhlcvAsync(string symbol, DateTime startTime, DateTime endTime);

    /// <summary>
    /// Поиск всех диапазонов дыр в базе с самой первой записи
    /// </summary>
    /// <param name="symbol">Пара по которой ищем дыру</param>
    /// <param name="minGapInSeconds">Временной диапазон для дыры</param>
    /// <returns></returns>
    Task<IEnumerable<DataGap>> FindTradeGapsAsync(string symbol, int minGapInSeconds);
}
