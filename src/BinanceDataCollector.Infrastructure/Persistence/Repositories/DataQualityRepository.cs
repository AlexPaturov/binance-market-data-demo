using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class DataQualityRepository : IDataQualityRepository
{
    private readonly string _connectionString;

    public DataQualityRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task<DataQualityReport> CheckSymbolMonthAsync(string symbol, int year, int month)
    {
        var periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = periodStart.AddMonths(1);
        var fromMs = new DateTimeOffset(periodStart).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(periodEnd).ToUnixTimeMilliseconds();

        // Single pass: gaps, invalid prices, outliers (5-sigma), trade count.
        // LAG on TradeId detects sequence breaks; cross join with price_stats is O(1).
        const string sql = @"
            WITH ordered AS (
                SELECT
                    ""TradeId"",
                    ""Price"",
                    ""Quantity"",
                    LAG(""TradeId"") OVER (ORDER BY ""TradeId"") AS prev_id
                FROM public.""Trades""
                WHERE ""Symbol"" = @Symbol
                  AND ""TradeTime"" >= @FromMs
                  AND ""TradeTime"" < @ToMs
            ),
            price_stats AS (
                SELECT AVG(""Price"") AS avg_p, STDDEV(""Price"") AS std_p FROM ordered
            )
            SELECT
                COUNT(*)                                                                               AS trade_count,
                COUNT(*) FILTER (WHERE prev_id IS NOT NULL AND ""TradeId"" > prev_id + 1)             AS gap_count,
                COUNT(*) FILTER (WHERE ""Price"" <= 0 OR ""Quantity"" <= 0)                           AS invalid_price_count,
                COUNT(*) FILTER (WHERE ps.std_p > 0 AND ABS(o.""Price"" - ps.avg_p) > 5 * ps.std_p) AS outlier_count
            FROM ordered o
            CROSS JOIN price_stats ps;";

        using var db = Connection;
        var row = await db.QuerySingleAsync<(long TradeCount, int GapCount, int InvalidPriceCount, int OutlierCount)>(
            sql, new { Symbol = symbol, FromMs = fromMs, ToMs = toMs }, commandTimeout: 300);

        var status = row.TradeCount == 0 || row.InvalidPriceCount > 0 ? "error"
                   : row.GapCount > 0 || row.OutlierCount > 0         ? "warning"
                   : "ok";

        return new DataQualityReport
        {
            Symbol           = symbol,
            PeriodMonth      = periodStart,
            TradeCount       = row.TradeCount,
            GapCount         = row.GapCount,
            InvalidPriceCount = row.InvalidPriceCount,
            OutlierCount     = row.OutlierCount,
            Status           = status,
            CheckedAt        = DateTime.UtcNow
        };
    }

    public async Task UpsertReportAsync(DataQualityReport report)
    {
        const string sql = @"
            INSERT INTO public.""DataQualityReports""
                (""Symbol"", ""PeriodMonth"", ""TradeCount"", ""GapCount"", ""InvalidPriceCount"", ""OutlierCount"", ""Status"", ""CheckedAt"")
            VALUES
                (@Symbol, @PeriodMonth, @TradeCount, @GapCount, @InvalidPriceCount, @OutlierCount, @Status, @CheckedAt)
            ON CONFLICT (""Symbol"", ""PeriodMonth"") DO UPDATE SET
                ""TradeCount""        = EXCLUDED.""TradeCount"",
                ""GapCount""          = EXCLUDED.""GapCount"",
                ""InvalidPriceCount"" = EXCLUDED.""InvalidPriceCount"",
                ""OutlierCount""      = EXCLUDED.""OutlierCount"",
                ""Status""            = EXCLUDED.""Status"",
                ""CheckedAt""         = EXCLUDED.""CheckedAt"";";

        using var db = Connection;
        await db.ExecuteAsync(sql, report);
    }

    public async Task<IEnumerable<DataQualityReport>> GetReportsAsync(string? symbol = null, string? status = null)
    {
        var where = new List<string>();
        if (symbol is not null) where.Add(@"""Symbol"" = @Symbol");
        if (status is not null) where.Add(@"""Status"" = @Status");
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        var sql = $@"
            SELECT * FROM public.""DataQualityReports""
            {whereClause}
            ORDER BY ""PeriodMonth"" DESC, ""Symbol"" ASC;";

        using var db = Connection;
        return await db.QueryAsync<DataQualityReport>(sql, new { Symbol = symbol, Status = status });
    }
}
