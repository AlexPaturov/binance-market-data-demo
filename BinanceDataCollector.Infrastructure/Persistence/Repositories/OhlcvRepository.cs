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

    public async Task<IEnumerable<Ohlcv>> ClaimNewKlinesForProcessingAsync(int batchSize)
    {
        using var db = Connection;
        const string sql = @"
            WITH candidates AS (
                -- 1. Находим кандидатов для обработки
                SELECT ""Symbol"", ""OpenTime""
                FROM public.""Ohlcv_1min""
                WHERE ""ProcessingStatus"" = 'new'
                ORDER BY ""OpenTime"" ASC
                LIMIT @BatchSize
                -- Блокируем строки, чтобы другой воркер их не тронул
                FOR UPDATE SKIP LOCKED 
            ),
            updated AS (
                -- 2. Атомарно обновляем их статус на 'processing'
                UPDATE public.""Ohlcv_1min""
                SET ""ProcessingStatus"" = 'processing'
                WHERE (""Symbol"", ""OpenTime"") IN (SELECT ""Symbol"", ""OpenTime"" FROM candidates)
                -- 3. Возвращаем обновленные строки
                RETURNING *
            )
            SELECT * FROM updated ORDER BY ""OpenTime"" ASC;
        ";

        return await db.QueryAsync<Ohlcv>(sql, new { BatchSize = batchSize }, commandTimeout: 300);
    }

    public async Task MarkKlinesAsProcessedAsync(IEnumerable<long> openTimes)
    {
        using var db = Connection;
        const string sql = @"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed' 
                         WHERE ""OpenTime"" = ANY(@OpenTimes)";
        await db.ExecuteAsync(sql, new { OpenTimes = openTimes.ToList() });
    }

    public async Task<IEnumerable<Ohlcv>> GetWarmupKlinesAsync(string symbol, long beforeTime, int limit)
    {
        using var db = Connection;

        // Этот SQL-запрос выбирает 'limit' свечей для указанного символа,
        // которые произошли до указанного времени, и сортирует их по убыванию,
        // чтобы получить самые последние из "старых".
        const string sql = @"
        SELECT * 
        FROM public.""Ohlcv_1min"" 
        WHERE ""Symbol"" = @Symbol AND ""OpenTime"" < @BeforeTime 
        ORDER BY ""OpenTime"" DESC 
        LIMIT @Limit";

        return await db.QueryAsync<Ohlcv>(sql, new { Symbol = symbol, BeforeTime = beforeTime, Limit = limit });
    }

}
