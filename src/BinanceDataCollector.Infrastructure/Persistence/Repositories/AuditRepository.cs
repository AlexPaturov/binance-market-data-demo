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
        
        return await db.QuerySingleOrDefaultAsync<ProcessWatermark?>(sql);
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
