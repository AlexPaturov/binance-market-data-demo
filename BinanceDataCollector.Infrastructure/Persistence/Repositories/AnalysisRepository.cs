using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public  class AnalysisRepository : IAnalysisRepository
{
    private readonly string _connectionString;

    public AnalysisRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
         ?? throw new InvalidOperationException("Connection string not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    /// <summary>
    /// МЕТОД ДЛЯ РАСЧЕТА CVD
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="startTime"></param>
    /// <param name="endTime"></param>
    /// <returns></returns>
    public async Task<IEnumerable<CvdResult>> GetCvdForOhlcvAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        var startTimeMs = new DateTimeOffset(startTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        var endTimeMs = new DateTimeOffset(endTime.ToUniversalTime()).ToUnixTimeMilliseconds();

        const string sql = @"
        WITH VolumeDelta AS (
            SELECT
                ""TradeTime"",
                CASE WHEN ""IsBuyerMaker"" = false THEN ""Quantity"" ELSE -""Quantity"" END as ""Delta""
            FROM public.""Trades""
            WHERE ""Symbol"" = @Symbol AND ""TradeTime"" >= @StartTimeMs AND ""TradeTime"" <= @EndTimeMs
        ),
        CumulativeDelta AS (
            SELECT
                ""TradeTime"",
                SUM(""Delta"") OVER (ORDER BY ""TradeTime"" ASC) as ""Cvd""
            FROM VolumeDelta
        )
        -- Агрегируем CVD по минутам, беря последнее значение за каждую минуту
        SELECT
            ( ""TradeTime"" / 60000 ) * 60000 as ""OpenTime"",
            (array_agg(""Cvd"" ORDER BY ""TradeTime"" DESC))[1] as ""Cvd""
        FROM CumulativeDelta
        GROUP BY 1
        ORDER BY 1;
    ";

        using var db = Connection;
        return await db.QueryAsync<CvdResult>(sql, new { Symbol = symbol, StartTimeMs = startTimeMs, EndTimeMs = endTimeMs });
    }



    /// <summary>
    ///  Получает статистику по качеству данных (статусам блоков аудита)
    ///  для указанного символа и временного диапазона.
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<IEnumerable<DataQualityStat>> GetDataQualityStatsAsync(string symbol, DateOnly startDate, DateOnly endDate)
    {
        using var db = Connection;
        const string sql = "SELECT * FROM public.sp_get_data_quality_stats(@Symbol, @StartDate, @EndDate)";

        var parameters = new
        {
            Symbol = symbol,
            StartDate = startDate.ToDateTime(TimeOnly.MinValue), // Преобразуем DateOnly в DateTime для Dapper/Npgsql
            EndDate = endDate.ToDateTime(TimeOnly.MinValue)
        };

        return await db.QueryAsync<DataQualityStat>(sql, parameters);
    }

}
