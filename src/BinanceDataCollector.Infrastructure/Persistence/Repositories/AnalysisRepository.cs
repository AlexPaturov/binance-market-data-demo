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

        using var db = Connection;

        // CVD считается ТОЛЬКО по свечам: дельту минуты уже посчитала агрегация в том же
        // проходе по тикам, что и OHLCV (миграция 012). Этот метод тики не читает вовсе.
        //
        // История, почему здесь нет чтения тиков даже как запасного пути: скан тиков
        // на фоне импорта архивов не укладывался ни в 30 с (умолчание Npgsql, 26 отмен
        // за 20 минут), ни в 600 с — а зависший скан продолжал жевать диск на сервере
        // и после отвала клиента. 16.07.2026 три таких скана остановили слив очереди
        // агрегации до нуля. Тиковый путь для CVD запрещён по конструкции.
        //
        // NULL в дельте — «не посчитана»: свеча построена до миграции 012 или записана
        // без тиковых данных (klines API). Одна неизвестная минута делает неверными все
        // накопленные суммы после себя, поэтому такое окно возвращается пустым — у этих
        // свечей в этом проходе CVD не будет. Потеря временная: минуты из очереди
        // переагрегируются, дельта заполняется, свеча снова 'new' — фичи пересчитаются
        // уже с CVD.
        const string hasUncomputedSql = @"
        SELECT EXISTS (
            SELECT 1 FROM public.""Ohlcv_1min""
            WHERE ""Symbol"" = @Symbol
              AND ""OpenTime"" >= @StartTimeMs AND ""OpenTime"" < @EndTimeMs
              AND ""CvdDelta"" IS NULL);";

        var hasUncomputed = await db.ExecuteScalarAsync<bool>(hasUncomputedSql,
            new { Symbol = symbol, StartTimeMs = startTimeMs, EndTimeMs = endTimeMs },
            commandTimeout: 60);

        if (hasUncomputed)
        {
            return Enumerable.Empty<CvdResult>();
        }

        const string candleSql = @"
        SELECT
            ""OpenTime"",
            SUM(""CvdDelta"") OVER (ORDER BY ""OpenTime"") as ""Cvd""
        FROM public.""Ohlcv_1min""
        WHERE ""Symbol"" = @Symbol
          AND ""OpenTime"" >= @StartTimeMs AND ""OpenTime"" < @EndTimeMs
        ORDER BY ""OpenTime"";";

        return await db.QueryAsync<CvdResult>(candleSql,
            new { Symbol = symbol, StartTimeMs = startTimeMs, EndTimeMs = endTimeMs },
            commandTimeout: 60);
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
