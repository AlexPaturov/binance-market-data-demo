using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Integration test for <see cref="TradeRepository"/> against a real PostgreSQL 16
/// container. The container is initialized with the exact schema baseline that ships
/// in docker/postgres/init/02_schema.sql, so the partitioned <c>Trades</c> table and the
/// stored procedures (<c>sp_ensure_trades_partition</c>, <c>sp_bulk_insert_trades</c>) are
/// exercised end to end.
/// </summary>
public sealed class TradeRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        // Apply the shipped schema baseline to the fresh container.
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
    public async Task BulkInsertAsync_PersistsTrades_RetrievableByIdAndLatest()
    {
        const string symbol = "BTCUSDT";
        var trades = new[]
        {
            NewTrade(1, symbol, price: 100.5m, tradeTime: TradeTime(minuteOffset: 0)),
            NewTrade(2, symbol, price: 101.0m, tradeTime: TradeTime(minuteOffset: 1)),
            NewTrade(3, symbol, price: 102.0m, tradeTime: TradeTime(minuteOffset: 2)),
        };

        await _repository.BulkInsertAsync(trades);

        var byId = await _repository.GetTradeByIdAsync(2, symbol);
        Assert.NotNull(byId);
        Assert.Equal(symbol, byId!.Symbol);
        Assert.Equal(101.0m, byId.Price);

        var latest = (await _repository.GetLatestTradesAsync(symbol, count: 10)).ToList();
        Assert.Equal(3, latest.Count);
        // GetLatestTradesAsync orders by TradeTime DESC.
        Assert.Equal(3, latest[0].TradeId);
        Assert.Equal(1, latest[^1].TradeId);
    }

    [Fact]
    public async Task BulkInsertAsync_IsIdempotent_OnRepeatedInsert()
    {
        const string symbol = "ETHUSDT";
        var trades = new[]
        {
            NewTrade(10, symbol, price: 50m, tradeTime: TradeTime(minuteOffset: 0)),
            NewTrade(11, symbol, price: 51m, tradeTime: TradeTime(minuteOffset: 1)),
        };

        await _repository.BulkInsertAsync(trades);
        await _repository.BulkInsertAsync(trades); // ON CONFLICT ("TradeId","Symbol") DO NOTHING

        var latest = (await _repository.GetLatestTradesAsync(symbol, count: 10)).ToList();
        Assert.Equal(2, latest.Count);
    }

    // 2025-01-01T00:00:00Z in unix milliseconds — lands in the Trades_2025_01 partition.
    private const long Jan2025Ms = 1_735_689_600_000;

    private static long TradeTime(int minuteOffset) => Jan2025Ms + minuteOffset * 60_000L;

    private static Trade NewTrade(long tradeId, string symbol, decimal price, long tradeTime) => new()
    {
        TradeId = tradeId,
        Symbol = symbol,
        Price = price,
        Quantity = 1.0m,
        QuoteQuantity = price,
        TradeTime = tradeTime,
        IsBuyerMaker = false,
        IsBestMatch = true,
    };
}
