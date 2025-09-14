using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IAnalysisRepository
{
    Task<IEnumerable<CvdResult>> GetCvdForOhlcvAsync(string symbol, DateTime startTime, DateTime endTime);


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

    Task<List<DataGap>> FindGapsInWindowAsync(string symbol, long startTradeId, long endTradeId);


}
