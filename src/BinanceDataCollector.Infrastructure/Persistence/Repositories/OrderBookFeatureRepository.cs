using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class OrderBookFeatureRepository : IOrderBookFeatureRepository
{
    private readonly string _connectionString;

    public OrderBookFeatureRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task BulkUpsertAsync(IEnumerable<OrderBookFeature> features)
    {
        var list = features.ToList();
        if (list.Count == 0) return;

        using var db = Connection;

        // Партиция под месяц может ещё не существовать.
        await db.ExecuteAsync(
            "SELECT public.sp_ensure_month_partitions(@OpenTime)",
            new { list[0].OpenTime },
            commandTimeout: 30);

        const string sql = @"
            INSERT INTO public.""OrderBook_Features"" (
                ""Symbol"", ""OpenTime"", ""MidPrice"", ""BestBid"", ""BestAsk"",
                ""SpreadAbs"", ""SpreadBps"", ""Imbalance"",
                ""BidDepth01"", ""AskDepth01"", ""BidDepth05"", ""AskDepth05"",
                ""BidDepth10"", ""AskDepth10"",
                ""MaxBidWall"", ""MaxBidWallDistBps"", ""MaxAskWall"", ""MaxAskWallDistBps"",
                ""UpdateCount"", ""SampleCount""
            ) VALUES (
                @Symbol, @OpenTime, @MidPrice, @BestBid, @BestAsk,
                @SpreadAbs, @SpreadBps, @Imbalance,
                @BidDepth01, @AskDepth01, @BidDepth05, @AskDepth05,
                @BidDepth10, @AskDepth10,
                @MaxBidWall, @MaxBidWallDistBps, @MaxAskWall, @MaxAskWallDistBps,
                @UpdateCount, @SampleCount
            )
            ON CONFLICT (""Symbol"", ""OpenTime"") DO UPDATE SET
                ""MidPrice""          = EXCLUDED.""MidPrice"",
                ""BestBid""           = EXCLUDED.""BestBid"",
                ""BestAsk""           = EXCLUDED.""BestAsk"",
                ""SpreadAbs""         = EXCLUDED.""SpreadAbs"",
                ""SpreadBps""         = EXCLUDED.""SpreadBps"",
                ""Imbalance""         = EXCLUDED.""Imbalance"",
                ""BidDepth01""        = EXCLUDED.""BidDepth01"",
                ""AskDepth01""        = EXCLUDED.""AskDepth01"",
                ""BidDepth05""        = EXCLUDED.""BidDepth05"",
                ""AskDepth05""        = EXCLUDED.""AskDepth05"",
                ""BidDepth10""        = EXCLUDED.""BidDepth10"",
                ""AskDepth10""        = EXCLUDED.""AskDepth10"",
                ""MaxBidWall""        = EXCLUDED.""MaxBidWall"",
                ""MaxBidWallDistBps"" = EXCLUDED.""MaxBidWallDistBps"",
                ""MaxAskWall""        = EXCLUDED.""MaxAskWall"",
                ""MaxAskWallDistBps"" = EXCLUDED.""MaxAskWallDistBps"",
                ""UpdateCount""       = EXCLUDED.""UpdateCount"",
                ""SampleCount""       = EXCLUDED.""SampleCount"";";

        await db.ExecuteAsync(sql, list, commandTimeout: 120);
    }

    public async Task<IEnumerable<OrderBookFeature>> GetAsync(string symbol, long fromMs, long toMs)
    {
        using var db = Connection;

        const string sql = @"
            SELECT * FROM public.""OrderBook_Features""
            WHERE ""Symbol"" = @Symbol AND ""OpenTime"" >= @FromMs AND ""OpenTime"" < @ToMs
            ORDER BY ""OpenTime"";";

        return await db.QueryAsync<OrderBookFeature>(sql,
            new { Symbol = symbol, FromMs = fromMs, ToMs = toMs }, commandTimeout: 60);
    }
}
