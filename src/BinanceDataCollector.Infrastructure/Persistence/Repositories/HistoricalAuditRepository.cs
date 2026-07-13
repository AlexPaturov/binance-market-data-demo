using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class HistoricalAuditRepository : IHistoricalAuditRepository
{
    private readonly string _connectionString;

    public HistoricalAuditRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task InitializeAuditForNewSymbolsAsync()
    {
        using var db = Connection;

        // Находит все символы, у которых есть сделки, но нет записи в HistoricalAudit_Watermarks,
        // и создает для них начальную вотермарку.
        //
        // Старт — не раньше границы ретенции: ниже неё партиций нет и создать их нельзя.
        // Иначе аудитор нашёл бы «дыру» там, где данных не должно быть, полез бы качать
        // архивы и упёрся в отказ вставки — и так по кругу.
        const string sql = @"
            INSERT INTO public.""HistoricalAudit_Watermarks""
                (""Symbol"", ""LastChecked_TradeId"", ""LastChecked_Timestamp"", ""Status"", ""LastAttempt_UTC"")
            SELECT
                ts.""Symbol"",
                0, -- Начинаем с самого начала
                GREATEST(
                    (EXTRACT(EPOCH FROM ts.""DateAdded"") * 1000)::BIGINT - 1,
                    public.fn_retention_floor_ms()
                ),
                'Pending',
                NOW() AT TIME ZONE 'utc'
            FROM public.""TrackedSymbols"" ts
            -- Выбираем только те символы, которых еще нет в вотермарках
            WHERE NOT EXISTS (
                SELECT 1 
                FROM public.""HistoricalAudit_Watermarks"" w 
                WHERE w.""Symbol"" = ts.""Symbol""
            )
            ON CONFLICT (""Symbol"") DO NOTHING;
        ";

        await db.ExecuteAsync(sql, commandTimeout: 300);
    }

    public async Task<IEnumerable<HistoricalWatermark>> GetSymbolsToAuditAsync(int batchSize, int maxRetries, TimeSpan retryInterval)
    {
        using var db = Connection;

        // Выбираем "ожидающие" блоки и "сбойные", которые не трогали достаточно долго
        // и у которых не превышен лимит попыток.
        const string sql = @"
            SELECT 
                ""Symbol"", 
                ""LastChecked_TradeId"", 
                ""LastChecked_Timestamp"",
                ""Status"",
                ""RetryCount"",
                ""LastAttempt_UTC""
            FROM public.""HistoricalAudit_Watermarks""
            WHERE
                ""Status"" = 'Pending' 
                OR 
                (""Status"" = 'Failed' AND ""RetryCount"" < @MaxRetries AND ""LastAttempt_UTC"" < NOW() AT TIME ZONE 'utc' - @RetryInterval)
            ORDER BY ""LastChecked_Timestamp"" ASC -- Начинаем с самых старых
            LIMIT @BatchSize;
        ";
        return await db.QueryAsync<HistoricalWatermark>(sql, new
        {
            BatchSize = batchSize,
            MaxRetries = maxRetries,
            RetryInterval = retryInterval
        });
    }

    public async Task UpdateWatermarkAsync(string symbol, long newTradeId, long newTimestamp, string newStatus, bool incrementRetryCount)
    {
        using var db = Connection;

        // Динамически строим SQL, чтобы инкрементировать счетчик только при необходимости.
        string sql = @"
            UPDATE public.""HistoricalAudit_Watermarks""
            SET 
                ""LastChecked_TradeId"" = @NewTradeId,
                ""LastChecked_Timestamp"" = @NewTimestamp,
                ""Status"" = @NewStatus,
                ""RetryCount"" = CASE WHEN @IncrementRetry THEN ""RetryCount"" + 1 ELSE 0 END, -- Сбрасываем счетчик при успехе
                ""LastAttempt_UTC"" = NOW() AT TIME ZONE 'utc'
            WHERE ""Symbol"" = @Symbol;
        ";

        await db.ExecuteAsync(sql, new
        {
            Symbol = symbol,
            NewTradeId = newTradeId,
            NewTimestamp = newTimestamp,
            NewStatus = newStatus,
            IncrementRetry = incrementRetryCount
        });
    }
}
