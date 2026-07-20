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
    private readonly ITradeRepository _tradeRepository;

    public DatabaseMonitoringService(
        IConfiguration configuration,
        ILogger<DatabaseMonitoringService> logger,
        ITradeRepository tradeRepository)
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
        _tradeRepository = tradeRepository;
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

    /// <summary>
    /// Помесячная сводка Trades для отдельной панели с собственным авто-обновлением (раз в 120 с),
    /// поэтому вынесена из GetDatabaseDetailsAsync (та зовётся 5-сек. рефрешем DB-панелей).
    /// Мягко деградирует к пустому списку; осмысленна только для market_analytics.
    /// </summary>
    public Task<List<MonthPartitionInfo>> GetMonthPartitionsAsync(string databaseName)
    {
        var dbConnectionString = new NpgsqlConnectionStringBuilder(
            _configuration.GetConnectionString("DefaultConnection")) { Database = databaseName }.ConnectionString;

        return SafeQueryAsync(() => QueryMonthsAsync(dbConnectionString),
            new List<MonthPartitionInfo>(), "months");
    }

    /// <summary>
    /// Запрос помесячной сводки: размер, tablespace (hot SSD / cold HDD), печать месяца
    /// (MonthSeal) и — для незапечатанных — причина. Подусловия печати повторяют
    /// fn_month_data_complete (миграция 015); «backfill в полёте» здесь не вычислим
    /// (очередь в jobs-БД) — его добавляет сборка State отдельным запросом.
    /// </summary>
    private async Task<List<MonthPartitionInfo>> QueryMonthsAsync(string dbConnectionString)
    {
        const string sql = @"
            WITH cov AS (
                SELECT date_trunc('month', ""TradeDate"")::date AS period, ""Symbol"",
                       min(""TradeDate"") AS lo, max(""TradeDate"") AS hi, count(*) AS n
                FROM public.""ArchiveImportLog""
                GROUP BY 1, 2
            ),
            parts AS (
                SELECT c.oid AS oid,
                       to_date(substring(c.relname FROM 'Trades_(\d{4}_\d{2})'), 'YYYY_MM') AS period,
                       (COALESCE(t.spcname, 'pg_default') = 'cold') AS on_cold
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
                LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
                WHERE c.relkind = 'r' AND c.relname ~ '^Trades_\d{4}_\d{2}$'
            )
            SELECT
                to_char(p.period, 'YYYY-MM')                     AS ""Month"",
                pg_size_pretty(pg_total_relation_size(p.oid))    AS ""Size"",
                p.on_cold                                        AS ""OnCold"",
                (s.""PeriodMonth"" IS NOT NULL)                  AS ""Sealed"",
                s.""SealedAt""                                   AS ""SealedAt"",
                (p.period = date_trunc('month', now() AT TIME ZONE 'UTC')::date) AS ""IsCurrentMonth"",
                CASE
                    WHEN NOT EXISTS (SELECT 1 FROM cov WHERE cov.period = p.period)
                        THEN 'no_import'
                    WHEN EXISTS (SELECT 1 FROM cov WHERE cov.period = p.period AND cov.n <> (cov.hi - cov.lo + 1))
                        THEN 'coverage_gap'
                    WHEN EXISTS (
                        SELECT 1 FROM public.""DirtyMinutes"" dm
                        WHERE dm.""OpenTime"" >= (extract(epoch FROM p.period)::bigint * 1000)
                          AND dm.""OpenTime"" <  (extract(epoch FROM (p.period + interval '1 month'))::bigint * 1000))
                        THEN 'not_aggregated'
                    WHEN EXISTS (
                        SELECT 1 FROM public.""Ohlcv_1min"" o
                        WHERE o.""OpenTime"" >= (extract(epoch FROM p.period)::bigint * 1000)
                          AND o.""OpenTime"" <  (extract(epoch FROM (p.period + interval '1 month'))::bigint * 1000)
                          AND o.""ProcessingStatus"" <> 'processed')
                        THEN 'candles_pending'
                    ELSE NULL
                END                                              AS ""ReasonCode""
            FROM parts p
            LEFT JOIN public.""MonthSeal"" s ON s.""PeriodMonth"" = p.period
            -- Партиции создаются впрок на месяцы вперёд; будущие (пустые) в сводке не нужны.
            WHERE p.period <= date_trunc('month', now() AT TIME ZONE 'UTC')::date
            ORDER BY p.period DESC;";

        await using var connection = new NpgsqlConnection(dbConnectionString);
        var months = (await connection.QueryAsync<MonthPartitionInfo>(
            new CommandDefinition(sql, commandTimeout: QueryTimeoutSeconds))).ToList();

        if (months.Count == 0)
            return months;

        // «backfill в полёте» — глобальный флаг из jobs-БД: воркер при нём вообще не печатает.
        // Нужен только чтобы отличить «импорт идёт» от «ждёт воркера» у готовых, но не запечатанных.
        var importInFlight = await _tradeRepository.IsArchiveImportInFlightAsync();
        foreach (var m in months)
            m.State = BuildMonthState(m, importInFlight);

        return months;
    }

    // Собирает подпись состояния месяца из печати, tablespace-независимых причин и
    // глобального флага импорта. Порядок ветвей повторяет критерий печати.
    private static string BuildMonthState(MonthPartitionInfo m, bool importInFlight)
    {
        if (m.Sealed)
            return m.SealedAt.HasValue ? $"Sealed {m.SealedAt:yyyy-MM-dd}" : "Sealed";
        if (m.IsCurrentMonth)
            return "Current";

        return m.ReasonCode switch
        {
            "no_import"       => "Нет импорта",
            "coverage_gap"    => "Дыра в покрытии",
            "not_aggregated"  => "Не сагрегировано",
            "candles_pending" => "Свечи не обработаны",
            // Данные готовы, печати ещё нет: либо импорт занял очередь, либо воркер не отработал.
            _                 => importInFlight ? "Готово, импорт идёт" : "Готово, ждёт воркера"
        };
    }
}