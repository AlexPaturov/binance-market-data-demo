using BinanceDataCollector.Application.Analytics.Models;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

/// <summary>
/// Свечи старших таймфреймов собираются на лету из `Ohlcv_1min` — отдельных таблиц
/// под 15м/1ч/4ч/1д/1нед в схеме нет.
/// </summary>
public class ChartRepository : IChartRepository
{
    private const int QueryTimeoutSeconds = 60;

    private readonly string _connectionString;

    public ChartRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    // Open — цена первой минуты бара, Close — последней; High/Low/Volume — агрегаты.
    // Сетка баров: floor((t - offset) / bucket) * bucket + offset.
    private const string BucketExpr =
        @"((((""OpenTime"" - @Offset) / @Bucket) * @Bucket) + @Offset)";

    private static string AggregateSql(string extraWhere, string orderLimit) => $@"
        WITH bucketed AS (
            SELECT
                {BucketExpr} AS bucket_time,
                ""OpenTime"", ""OpenPrice"", ""HighPrice"", ""LowPrice"", ""ClosePrice"", ""Volume""
            FROM public.""Ohlcv_1min""
            WHERE ""Symbol"" = @Symbol {extraWhere}
        ),
        agg AS (
            SELECT
                bucket_time                                                AS ""OpenTime"",
                (array_agg(""OpenPrice""  ORDER BY ""OpenTime"" ASC))[1]  AS ""OpenPrice"",
                MAX(""HighPrice"")                                         AS ""HighPrice"",
                MIN(""LowPrice"")                                          AS ""LowPrice"",
                (array_agg(""ClosePrice"" ORDER BY ""OpenTime"" DESC))[1] AS ""ClosePrice"",
                SUM(""Volume"")                                            AS ""Volume""
            FROM bucketed
            GROUP BY bucket_time
        )
        SELECT * FROM agg
        {orderLimit};";

    public async Task<List<ChartCandle>> GetCandlesAsync(string symbol, string timeframe, int limit)
    {
        Validate(timeframe);
        limit = Math.Clamp(limit, 1, ChartTimeframes.MaxLimit);

        // Берём последние N баров, затем возвращаем в хронологическом порядке.
        var sql = AggregateSql(extraWhere: "", orderLimit: @"ORDER BY ""OpenTime"" DESC LIMIT @Limit");

        using var db = Connection;
        var rows = await db.QueryAsync<ChartCandle>(sql, new
        {
            Symbol = symbol,
            Bucket = ChartTimeframes.BucketMs(timeframe),
            Offset = ChartTimeframes.AlignmentOffsetMs(timeframe),
            Limit = limit
        }, commandTimeout: QueryTimeoutSeconds);

        return rows.OrderBy(c => c.OpenTime).ToList();
    }

    public async Task<List<ChartCandle>> GetCandlesSinceAsync(string symbol, string timeframe, long sinceMs)
    {
        Validate(timeframe);

        // Минутные свечи от начала запрошенного бара — иначе последний (незакрытый)
        // бар соберётся из огрызка данных.
        var sql = AggregateSql(
            extraWhere: @"AND ""OpenTime"" >= @SinceMs",
            orderLimit: @"ORDER BY ""OpenTime"" ASC LIMIT @Limit");

        using var db = Connection;
        var rows = await db.QueryAsync<ChartCandle>(sql, new
        {
            Symbol = symbol,
            Bucket = ChartTimeframes.BucketMs(timeframe),
            Offset = ChartTimeframes.AlignmentOffsetMs(timeframe),
            SinceMs = sinceMs,
            Limit = ChartTimeframes.MaxLimit
        }, commandTimeout: QueryTimeoutSeconds);

        return rows.ToList();
    }

    public async Task<List<IndicatorPoint>> GetCvdAsync(string symbol, string timeframe, long fromMs, long toMs)
    {
        Validate(timeframe);

        // CVD кумулятивен: значение на конец бара — это последнее минутное значение
        // внутри бара, а не сумма минутных.
        const string sql = @"
            WITH bucketed AS (
                SELECT
                    ((((""OpenTime"" - @Offset) / @Bucket) * @Bucket) + @Offset) AS bucket_time,
                    ""OpenTime"",
                    ""CVD""
                FROM public.""Ohlcv_Features""
                WHERE ""Symbol"" = @Symbol
                  AND ""OpenTime"" >= @FromMs
                  AND ""OpenTime"" <= @ToMs
                  AND ""CVD"" IS NOT NULL
            )
            SELECT
                bucket_time                                       AS ""OpenTime"",
                (array_agg(""CVD"" ORDER BY ""OpenTime"" DESC))[1] AS ""Value""
            FROM bucketed
            GROUP BY bucket_time
            ORDER BY bucket_time;";

        using var db = Connection;
        var rows = await db.QueryAsync<IndicatorPoint>(sql, new
        {
            Symbol = symbol,
            Bucket = ChartTimeframes.BucketMs(timeframe),
            Offset = ChartTimeframes.AlignmentOffsetMs(timeframe),
            FromMs = fromMs,
            ToMs = toMs
        }, commandTimeout: QueryTimeoutSeconds);

        return rows.ToList();
    }

    private static void Validate(string timeframe)
    {
        if (!ChartTimeframes.IsKnown(timeframe))
            throw new ArgumentException($"Неизвестный таймфрейм: {timeframe}");
    }
}
