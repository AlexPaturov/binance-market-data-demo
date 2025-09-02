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
        // Находит минимальную дату в Trades, максимальную в Audit_Blocks,
        // и генерирует все недостающие 3-дневные интервалы.
        const string sql = @"
            INSERT INTO public.""Audit_Blocks"" (""Symbol"", ""BlockStartDate"", ""Status"")
            WITH DateSeries AS (
                SELECT generate_series(
                    -- Начинаем с самой старой даты в Trades
                    (SELECT date_trunc('day', to_timestamp(MIN(""TradeTime"") / 1000.0)) FROM public.""Trades""),
                    -- Заканчиваем сегодняшним днем
                    date_trunc('day', NOW() AT TIME ZONE 'utc'),
                    '3 day'::interval
                )::date AS BlockDate
            ),
            Symbols AS (
                SELECT DISTINCT ""Symbol"" FROM public.""Trades""
            )
            SELECT
                s.""Symbol"",
                ds.BlockDate,
                'Pending' AS ""Status""
            FROM Symbols s
            CROSS JOIN DateSeries ds
            -- Вставляем только те комбинации (Символ, ДатаБлока), которых еще нет
            ON CONFLICT (""Symbol"", ""BlockStartDate"") DO NOTHING;
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
}