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

    /// <summary>
    ///  Перед тем как обучать модель на данных, например, за последний год, вы можете вызвать этот метод. 
    ///  Если он вернет вам записи со статусом Abandoned или Failed, вы будете знать, что ваш набор данных неполноценен, 
    ///  и сможете принять решение: либо исключить эти диапазоны, либо сначала попытаться "вылечить" их вручную.
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <returns></returns>
    Task<IEnumerable<DataQualityStat>> GetDataQualityStatsAsync(string symbol, DateOnly startDate, DateOnly endDate);

    /// <summary>
    /// для поиска разрывов в данных ТОЛЬКО в указанном временном окне
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="startTime"></param>
    /// <param name="endTime"></param>
    /// <returns></returns>
    Task<IEnumerable<DataGap>> FindGapsInWindowAsync(string symbol, DateTime startTime, DateTime endTime);
}
