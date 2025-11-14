using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class AuditRepository : IAuditRepository
{
    private readonly string _connectionString;

    public AuditRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    /// <summary>
    /// Находит самые старые торговые данные и генерирует недостающие блоки для аудита.
    /// </summary>
    public async Task GenerateNewAuditBlocksAsync()
    {
        using var db = Connection;

        const string sql = @"
            INSERT INTO public.""Audit_Blocks"" (""Symbol"", ""BlockStartDate"", ""Status"")
            WITH Symbols AS (
                SELECT ""Symbol"", date_trunc('day', ""DateAdded"")::date AS ""DateAdded""
                FROM public.""TrackedSymbols""
            ),
            ExistingBlocks AS (
                SELECT ""Symbol"", ""BlockStartDate""
                FROM public.""Audit_Blocks""
                WHERE ""Symbol"" IN (SELECT ""Symbol"" FROM Symbols)
            ),
            Gaps AS (
                SELECT ""Symbol"", ""PrevBlockDate"" + INTERVAL '3 day' AS ""GapStart""
                FROM (
                    SELECT ""Symbol"", ""BlockStartDate"", LAG(""BlockStartDate"", 1) OVER (PARTITION BY ""Symbol"" ORDER BY ""BlockStartDate"") AS ""PrevBlockDate""
                    FROM ExistingBlocks
                ) AS sub
                    WHERE (""BlockStartDate"" - ""PrevBlockDate"") > 3
                    UNION ALL
                    SELECT s.""Symbol"", s.""DateAdded"" AS ""GapStart""
                    FROM Symbols s
                    LEFT JOIN ExistingBlocks eb ON s.""Symbol"" = eb.""Symbol""
                    GROUP BY s.""Symbol"", s.""DateAdded""
                    HAVING MIN(eb.""BlockStartDate"") IS NULL OR MIN(eb.""BlockStartDate"") > s.""DateAdded""
                    UNION ALL
                    SELECT ""Symbol"", MAX(""BlockStartDate"") + INTERVAL '3 day' AS ""GapStart""
                    FROM ExistingBlocks
                    GROUP BY ""Symbol""
            ),
            DateSeries AS (
                SELECT g.""Symbol"", generate_series(g.""GapStart"", date_trunc('day', NOW() AT TIME ZONE 'utc'), '3 day'::interval)::date AS BlockDate
                FROM Gaps g
            )
            SELECT ds.""Symbol"", ds.BlockDate, 'Pending' AS ""Status""
            FROM DateSeries ds
            LEFT JOIN public.""Audit_Blocks"" existing_blocks ON ds.""Symbol"" = existing_blocks.""Symbol"" AND ds.BlockDate = existing_blocks.""BlockStartDate""
            WHERE existing_blocks.""Symbol"" IS NULL;
        ";

        await db.ExecuteAsync(sql, commandTimeout: 300);
    }

    /// <summary>
    /// Получает порцию блоков, которые нужно обработать.
    /// </summary>
    public async Task<IEnumerable<AuditBlock>> GetBlocksToProcessAsync(int maxRetries, int limit)
    {
        using var db = Connection;
        // Выбираем "ожидающие" блоки и "сбойные", которые не трогали больше суток
        const string sql = @"
            SELECT ""Symbol"", ""BlockStartDate""
            FROM public.""Audit_Blocks""
            WHERE
                ""Status"" = 'Pending' 
                OR 
                (""Status"" = 'Failed' AND ""LastAttempt"" < NOW() - INTERVAL '1 day' AND ""RetryCount"" < @MaxRetries)
            ORDER BY ""BlockStartDate"" ASC
            LIMIT @Limit;
        ";
        return await db.QueryAsync<AuditBlock>(sql, new { MaxRetries = maxRetries, Limit = limit });
    }

    /// <summary>
    /// Обновляет статус одного блока аудита.
    /// </summary>
    public async Task UpdateBlockStatusAsync(string symbol, DateTime blockStartDate, string newStatus, bool incrementRetryCount)
    {
        using var db = Connection;
        string sql;

        if (incrementRetryCount)
        {
            sql = @"
                UPDATE public.""Audit_Blocks""
                SET ""Status"" = @NewStatus, ""LastAttempt"" = NOW(), ""RetryCount"" = ""RetryCount"" + 1
                WHERE ""Symbol"" = @Symbol AND ""BlockStartDate"" = @BlockStartDate;
            ";
        }
        else
        {
            sql = @"
                UPDATE public.""Audit_Blocks""
                SET ""Status"" = @NewStatus, ""LastAttempt"" = NOW()
                WHERE ""Symbol"" = @Symbol AND ""BlockStartDate"" = @BlockStartDate;
            ";
        }

        await db.ExecuteAsync(sql, new
        {
            NewStatus = newStatus,
            Symbol = symbol,
            BlockStartDate = blockStartDate // Передаем как DateOnly/Date
        });
    }

    /// <summary>
    /// Получает текущее состояние (вотермарку) для процесса агрегации свечей.
    /// </summary>
    /// <remarks>
    /// Этот метод делает простой SELECT-запрос к таблице "Processing_Watermarks",
    /// чтобы узнать, на каком моменте остановилась последняя успешная агрегация.
    /// </remarks>
    /// <returns>Объект ProcessWatermark с последним состоянием.</returns>
    public async Task<ProcessWatermark> GetAggregationWatermarkAsync()
    {
        using var db = Connection;
        const string sql = @"
            SELECT ""ProcessName"", ""LastProcessedTimestamp"", ""Status""
            FROM public.""Processing_Watermarks""
            WHERE ""ProcessName"" = 'OhlcvAggregator';
        ";
        
        // Используем QuerySingleAsync, так как мы ожидаем, что запись ВСЕГДА существует.
        // Если ее нет, это будет исключением, что правильно.
        return await db.QuerySingleAsync<ProcessWatermark>(sql);
    }

    public async Task UpdateAggregationWatermarkAsync(long lastProcessedTimestamp, string status)
    {
        using var db = Connection;
        const string sql = @"
            UPDATE public.""Processing_Watermarks""
            SET
                ""LastProcessedTimestamp"" = @LastProcessedTimestamp,
                ""Status"" = @Status,
                ""LastUpdate_UTC"" = NOW() AT TIME ZONE 'utc'
            WHERE ""ProcessName"" = 'OhlcvAggregator';
        ";

        await db.ExecuteAsync(sql, new
        {
            LastProcessedTimestamp = lastProcessedTimestamp,
            Status = status
        });
    }
}
