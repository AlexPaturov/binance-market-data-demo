using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Models;
using BinanceDataCollector.Application.ViewModels;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BinanceDataCollector.Infrastructure.Services;

public class DatabaseMonitoringService : IDatabaseMonitoringService
{
    // Страница мониторинга не должна висеть и не должна ронять запрос в 500 из-за
    // транзиентной недоступности БД: короткий таймаут + мягкая деградация к «N/A».
    private const int QueryTimeoutSeconds = 10;

    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseMonitoringService> _logger;

    public DatabaseMonitoringService(IConfiguration configuration, ILogger<DatabaseMonitoringService> logger)
    {
        // Используем "служебное" подключение к базе 'postgres', так как из него можно получить размер любой другой базы.
        var originalConnectionString = configuration.GetConnectionString("DefaultConnection");
        var builder = new NpgsqlConnectionStringBuilder(originalConnectionString)
        {
            Database = "postgres" // Подключаемся к 'postgres'
        };
        _connectionString = builder.ConnectionString;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет запрос мониторинга, а при сбое возвращает fallback вместо исключения:
    /// временная недоступность БД деградирует до заглушки, а не в 500 на всей странице.
    /// </summary>
    private async Task<T> SafeQueryAsync<T>(Func<Task<T>> query, T fallback, string what)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database monitoring: '{What}' unavailable, degrading to fallback.", what);
            return fallback;
        }
    }
    
    public async Task<string> GetDatabaseSizeAsync(string databaseName)
    {
        const string sql = "SELECT pg_size_pretty(pg_database_size(@DatabaseName));";
        await using var connection = new NpgsqlConnection(_connectionString);
        var size = await connection.QuerySingleOrDefaultAsync<string>(sql, new { DatabaseName = databaseName });
        return size ?? "N/A"; // Возвращаем "N/A", если что-то пошло не так
    }

    public async Task<List<PostgresConnectionInfo>> GetActiveConnectionsAsync()
    {
        const string sql = @"
            SELECT
                datname AS DatabaseName,
                usename AS UserName,
                application_name AS ApplicationName,
                state AS State,
                COUNT(*) AS ConnectionCount
            FROM pg_stat_activity
            GROUP BY 1, 2, 3, 4
            ORDER BY ConnectionCount DESC;";
        
        await using var connection = new NpgsqlConnection(_connectionString);

        var connections = await connection.QueryAsync<PostgresConnectionInfo>(
            new CommandDefinition(sql, commandTimeout: QueryTimeoutSeconds));
        return connections.ToList();
    }
    
    public async Task<DatabaseDetailsViewModel> GetDatabaseDetailsAsync(string databaseName)
    {
        // --- ЗАПРОС РАЗМЕРОВ ТАБЛИЦ И ИНДЕКСОВ ---
        const string sizeSql = @"
            SELECT
                table_name AS TableName,
                pg_size_pretty(pg_table_size(table_name)) AS TableSize,
                pg_size_pretty(pg_indexes_size(table_name)) AS IndexSize,
                pg_size_pretty(pg_total_relation_size(table_name)) AS TotalSize
            FROM (
                SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) AS table_name
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ) AS all_tables
            ORDER BY pg_total_relation_size(table_name) DESC ;
        ";

        // --- ЗАПРОС ОБЩЕГО РАЗМЕРА ---
        const string totalSizeSql = "SELECT pg_size_pretty(pg_database_size(@DatabaseName));";

        // Подключаемся к ЦЕЛЕВОЙ базе для получения размеров таблиц
        var dbConnectionString = new NpgsqlConnectionStringBuilder(_configuration.GetConnectionString("DefaultConnection"))
            { Database = databaseName }.ConnectionString;

        // Три источника независимы: каждый со своим таймаутом и мягкой деградацией,
        // чтобы недоступность одного не роняла страницу и не блокировала остальные.
        var tableSizesTask = SafeQueryAsync(async () =>
        {
            await using var dbConnection = new NpgsqlConnection(dbConnectionString);
            var rows = await dbConnection.QueryAsync<TableSizeInfo>(
                new CommandDefinition(sizeSql, commandTimeout: QueryTimeoutSeconds));
            return rows.ToList();
        }, new List<TableSizeInfo>(), "table sizes");

        var totalSizeTask = SafeQueryAsync(async () =>
        {
            await using var serviceConnection = new NpgsqlConnection(_connectionString);
            return await serviceConnection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(totalSizeSql, new { DatabaseName = databaseName },
                    commandTimeout: QueryTimeoutSeconds)) ?? "N/A";
        }, "N/A", "total size");

        var connectionsTask = SafeQueryAsync(
            GetActiveConnectionsAsync, new List<PostgresConnectionInfo>(), "connections");

        await Task.WhenAll(tableSizesTask, totalSizeTask, connectionsTask);

        return new DatabaseDetailsViewModel
        {
            TableSizes = await tableSizesTask,
            TotalDatabaseSize = await totalSizeTask,
            Connections = await connectionsTask
        };
    }
}