using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class OhlcvRepository : IOhlcvRepository
{
    private readonly string _connectionString;

    public OhlcvRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Connection string not found.");
    }
    private IDbConnection Connection => new NpgsqlConnection(_connectionString);


    public async Task<IEnumerable<Ohlcv>> GetKlinesWithWarmupAsync(string symbol, long startTime, int warmupPeriod)
    {
        const string sql = @"
        (SELECT * FROM public.""Ohlcv_1min"" WHERE ""Symbol"" = @Symbol AND ""OpenTime"" < @StartTime ORDER BY ""OpenTime"" DESC LIMIT @WarmupPeriod)
        UNION ALL
        (SELECT * FROM public.""Ohlcv_1min"" WHERE ""Symbol"" = @Symbol AND ""OpenTime"" >= @StartTime)
        ORDER BY ""OpenTime"" ASC;
    ";
        using var db = Connection;
        return await db.QueryAsync<Ohlcv>(sql, new { Symbol = symbol, StartTime = startTime, WarmupPeriod = warmupPeriod });
    }
}
