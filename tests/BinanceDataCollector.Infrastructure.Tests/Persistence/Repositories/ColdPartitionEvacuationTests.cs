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

        var schemaSql = await File.ReadAllTextAsync("02_schema.sql");
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

    /// <summary>Грязные минуты — месяц ещё пересчитывается, агрегация будет читать его тики.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithDirtyMinutes()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);   // минута в очереди, не агрегирована

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Необработанные свечи — фичи месяца не досчитаны, месяц не закрыт.</summary>
    [Fact]
    public async Task Evacuate_SkipsMonth_WithUnprocessedCandles()
    {
        await InsertTradeAsync(1, Jan2026Ms + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);   // свечи остались 'new'

        Assert.Null(await EvacuateAsync());
    }

    /// <summary>Текущий месяц не эвакуируется никогда, каким бы «тихим» он ни был.</summary>
    [Fact]
    public async Task Evacuate_NeverTouchesCurrentMonth()
    {
        var currentMonthStart = new DateTimeOffset(
            DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        await InsertTradeAsync(1, currentMonthStart + 1_000);
        await _repository.AggregateDirtyMinutesAsync(100);
        await ExecuteAsync(@"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed';");

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

        var schemaSql = await File.ReadAllTextAsync("02_schema.sql");
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
