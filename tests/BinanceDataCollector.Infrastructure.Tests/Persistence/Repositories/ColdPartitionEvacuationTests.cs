using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Эвакуация закрытых месяцев Trades на холодное пространство (миграция 013).
/// Закрытый месяц — прошедший, без грязных минут, со всеми обработанными свечами;
/// всё остальное остаётся на горячем диске.
/// </summary>
public sealed class ColdPartitionEvacuationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _repository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;   // 2026-01-01T00:00:00Z — заведомо прошедший месяц

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _connectionString = _db.GetConnectionString();

        // Каталог холодного пространства внутри контейнера; владелец — postgres.
        await _db.ExecAsync(new[] { "mkdir", "-p", "/var/lib/postgresql/cold" });
        await _db.ExecAsync(new[] { "chown", "postgres:postgres", "/var/lib/postgresql/cold" });

        var schemaSql = await File.ReadAllTextAsync("02_baseline.sql");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(schemaSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(@"CREATE TABLESPACE cold LOCATION '/var/lib/postgresql/cold';");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString
            })
            .Build();

        _repository = new TradeRepository(configuration, NullLogger<TradeRepository>.Instance);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Evacuate_MovesClosedMonth_WithAllItsIndexes()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);
        await ExecuteAsync(@"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");
        await SealMonthAsync(Jan2026Ms);

        var moved = await EvacuateAsync();

        Assert.Equal("Trades_2026_01", moved);

        // Сама партиция и каждый её индекс — в холодном пространстве.
        var misplaced = await QuerySingleAsync<int>(@"
            SELECT count(*)::int
            FROM pg_class c
            WHERE c.relname LIKE 'Trades_2026_01%'
              AND c.reltablespace IS DISTINCT FROM (
                  SELECT oid FROM pg_tablespace WHERE spcname = 'cold');");
        Assert.Equal(0, misplaced);

        // Данные читаются как прежде.
        var ticks = await QuerySingleAsync<int>(
            @"SELECT count(*)::int FROM public.""Trades"" WHERE ""Symbol"" = 'BTCUSDT';");
        Assert.Equal(1, ticks);

        // Второй заход: закрытых месяцев в горячем пространстве больше нет.
        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Грязные минуты — месяц ещё пересчитывается: не закрыт, хоть и запечатан.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithDirtyMinutes()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);   // минута в очереди, не агрегирована
        await SealMonthAsync(Jan2026Ms);                // печать есть, но данные не готовы

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Необработанные свечи — индикаторы не досчитаны: не закрыт, хоть и запечатан.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithUnprocessedCandles()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);   // свечи остались 'new'
        await SealMonthAsync(Jan2026Ms);

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Нет покрытия в журнале — месяц не считается закрытым, даже с печатью.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithoutCoverage()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);
        await ExecuteAsync(@"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");
        // Печать без записи в журнал покрытия: fn_month_data_complete = false.
        await ExecuteAsync(@"INSERT INTO public.""MonthSeal"" (""PeriodMonth"") VALUES ('2026-01-01');");

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Текущий месяц не эвакуируется никогда, каким бы «закрытым» он ни выглядел.</summary>
    [Fact]
    public async Task Evacuate_NeverTouchesCurrentMonth()
    {
        var currentMonthStart = new DateTimeOffset(
            DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        await InsertTradeAsync(1, currentMonthStart + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);
        await ExecuteAsync(@"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");
        await SealMonthAsync(currentMonthStart);

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>
    /// Пустая партиция на cold возвращается на горячее пространство перед вставкой
    /// (миграция 016): снос очищает строки, но tablespace не меняет — без rehydrate
    /// переимпорт снесённого месяца шёл бы на холодный диск.
    /// </summary>
    [Fact]
    public async Task EnsurePartitions_ReturnsEmptyColdPartition_ToHotTablespace()
    {
        // Партиция пуста и «застряла» на cold (как после TRUNCATE эвакуированного месяца).
        await ExecuteAsync(@"ALTER TABLE public.""Trades_2026_02"" SET TABLESPACE cold;");

        // Путь вставки: BulkInsertAsync зовёт ensure перед записью.
        await ExecuteAsync(@"SELECT public.sp_ensure_month_partitions(1769904000000);");   // 2026-02-01

        var tablespace = await QuerySingleAsync<string?>(@"
            SELECT t.spcname FROM pg_class c
            LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            WHERE c.relname = 'Trades_2026_02';");
        Assert.Null(tablespace);   // NULL = базовое (горячее) пространство

        // Непустая партиция на cold НЕ трогается: закрытый месяц остаётся холодным.
        await InsertTradeAsync(10, 1769904000000 + 1_000);
        await ExecuteAsync(@"ALTER TABLE public.""Trades_2026_02"" SET TABLESPACE cold;");
        await ExecuteAsync(@"SELECT public.sp_ensure_month_partitions(1769904000000);");

        tablespace = await QuerySingleAsync<string?>(@"
            SELECT t.spcname FROM pg_class c
            LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            WHERE c.relname = 'Trades_2026_02';");
        Assert.Equal("cold", tablespace);
    }

    /// <summary>Дыра в покрытии месяца (пропущен день внутри диапазона) — не закрыт.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithCoverageGap()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);
        await ExecuteAsync(@"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");
        // Покрытие 1-е и 3-е января, 2-е пропущено → диапазон не сплошной.
        await SealMonthAsync(Jan2026Ms, 1, 3);

        Assert.Null(await EvacuateAsync());
    }

    // Эвакуацию планирует pg_cron через SELECT sp_evacuate_next_cold_partition();
    // в тестах зовём ту же функцию напрямую.
    private async Task<string?> EvacuateAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT public.sp_evacuate_next_cold_partition()");
    }

    // --- вспомогательное ---

    private async Task InsertTradeAsync(long id, long timeMs)
    {
        await ExecuteAsync(
            @"SELECT public.sp_bulk_insert_trades(
                  ARRAY[@Id]::bigint[], ARRAY['BTCUSDT']::varchar[], ARRAY[100.0]::numeric[],
                  ARRAY[1.0]::numeric[], ARRAY[100.0]::numeric[], ARRAY[@T]::bigint[],
                  ARRAY[false]::boolean[], ARRAY[true]::boolean[]);",
            new { Id = id, T = timeMs });
    }

    // Печать закрытого месяца + покрытие в журнале (то, что ставит воркер) — эвакуация
    // теперь гейтится на них.
    private async Task SealMonthAsync(long monthStartMs, params int[] days)
    {
        var m = DateTimeOffset.FromUnixTimeMilliseconds(monthStartMs).UtcDateTime;
        foreach (var d in days.Length > 0 ? days : new[] { 1 })
        {
            await ExecuteAsync(
                @"INSERT INTO public.""ArchiveImportLog"" (""Symbol"", ""TradeDate"")
                  VALUES ('BTCUSDT', @D) ON CONFLICT DO NOTHING;",
                new { D = new DateOnly(m.Year, m.Month, d) });
        }
        await ExecuteAsync(
            @"INSERT INTO public.""MonthSeal"" (""PeriodMonth"")
              VALUES (@M) ON CONFLICT DO NOTHING;",
            new { M = new DateOnly(m.Year, m.Month, 1) });
    }

    private async Task ExecuteAsync(string sql, object? param = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, param);
    }

    private async Task<T> QuerySingleAsync<T>(string sql, object? param = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<T>(sql, param);
    }
}

/// <summary>
/// Без пространства `cold` эвакуация молча бездействует: dev-окружения и тесты,
/// которым тиринг не нужен, не обязаны его создавать.
/// </summary>
public sealed class ColdPartitionEvacuationWithoutTablespaceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        var schemaSql = await File.ReadAllTextAsync("02_baseline.sql");
        await using (var connection = new NpgsqlConnection(_db.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(schemaSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.GetConnectionString()
            })
            .Build();

        _repository = new TradeRepository(configuration, NullLogger<TradeRepository>.Instance);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Evacuate_DoesNothing_WhenColdTablespaceIsAbsent()
    {
        Assert.Null(await EvacuateAsync());
    }

    private async Task<string?> EvacuateAsync()
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT public.sp_evacuate_next_cold_partition()");
    }
}
