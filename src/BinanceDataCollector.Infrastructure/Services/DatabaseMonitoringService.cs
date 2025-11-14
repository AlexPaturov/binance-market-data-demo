using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Application.Models;
using BinanceDataCollector.Application.ViewModels;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BinanceDataCollector.Infrastructure.Services;

public class DatabaseMonitoringService : IDatabaseMonitoringService
{
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    
    public DatabaseMonitoringService(IConfiguration configuration)
    {
        // Используем "служебное" подключение к базе 'postgres', так как из него можно получить размер любой другой базы.
        var originalConnectionString = configuration.GetConnectionString("DefaultConnection");
        var builder = new NpgsqlConnectionStringBuilder(originalConnectionString)
        {
            Database = "postgres" // Подключаемся к 'postgres'
        };
        _connectionString = builder.ConnectionString;
        _configuration = configuration;
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
        
        var connections = await connection.QueryAsync<PostgresConnectionInfo>(sql);
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

        // --- ЗАПРОС ПОДКЛЮЧЕНИЙ ---
        const string connectionsSql = "SELECT ..."; // старый запрос подключений

        // Подключаемся к ЦЕЛЕВОЙ базе для получения размеров таблиц
        var dbConnectionString = new NpgsqlConnectionStringBuilder(_configuration.GetConnectionString("DefaultConnection"))
            { Database = databaseName }.ConnectionString;

        // Подключаемся к СЛУЖЕБНОЙ базе для получения общего размера и коннектов
        var serviceConnectionString = new NpgsqlConnectionStringBuilder(_configuration.GetConnectionString("DefaultConnection"))
            { Database = "postgres" }.ConnectionString;

        await using var dbConnection = new NpgsqlConnection(dbConnectionString);
        await using var serviceConnection = new NpgsqlConnection(serviceConnectionString);

        // Выполняем все запросы асинхронно и параллельно
        var tableSizesTask = dbConnection.QueryAsync<TableSizeInfo>(sizeSql);
        var totalSizeTask = serviceConnection.QuerySingleOrDefaultAsync<string>(totalSizeSql, new { DatabaseName = databaseName });
        var connectionsTask = GetActiveConnectionsAsync(); // Вызываем существующий метод

        await Task.WhenAll(tableSizesTask, totalSizeTask, connectionsTask);

        return new DatabaseDetailsViewModel
        {
            TableSizes = (await tableSizesTask).ToList(),
            TotalDatabaseSize = (await totalSizeTask) ?? "N/A",
            Connections = await connectionsTask
        };
    }
}