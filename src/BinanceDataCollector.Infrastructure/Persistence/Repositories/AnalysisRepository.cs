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
        // Таймаут задан явно: умолчание Npgsql — 30 с, и на фоне импорта архивов запрос
        // в него не укладывался (16.07.2026: 26 отмен за 20 минут). Каждая отмена
        // прилетала в catch по символу и оставляла его без фич. 600 — потолок, принятый
        // в проекте для тяжёлых запросов по тикам (см. AggregateDirtyMinutesAsync).
        return await db.QueryAsync<CvdResult>(sql,
            new { Symbol = symbol, StartTimeMs = startTimeMs, EndTimeMs = endTimeMs },
            commandTimeout: 600);
    }

    public async Task<List<DataGap>> FindGapsInWindowAsync(string symbol, long startTradeId, long endTradeId)
    {
        using var db = Connection;
        const string sql = "SELECT * FROM public.sp_find_trade_id_gaps_in_window(@Symbol, @StartTradeId, @EndTradeId)";

        var gaps = await db.QueryAsync<DataGap>(sql, new
        {
            Symbol = symbol,
            StartTradeId = startTradeId,
            EndTradeId = endTradeId
        }, commandTimeout: 300); // <-- Ставим БОЛЬШОЙ таймаут (5 минут)

        return gaps.AsList();
    }
}
