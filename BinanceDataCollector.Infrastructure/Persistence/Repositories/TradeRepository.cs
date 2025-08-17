using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly string _connectionString;

    public TradeRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                        ?? throw new InvalidOperationException("Connection string not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task<Trade?> GetByIdAsync(long tradeId, string symbol)
    {
        using var db = Connection;
        const string sql = "SELECT * FROM \"Trades\" WHERE \"TradeId\" = @TradeId AND \"Symbol\" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<Trade>(sql, new { TradeId = tradeId, Symbol = symbol });
    }

    public async Task<IEnumerable<Trade>> GetLatestTradesAsync(string symbol, int count)
    {
        using var db = Connection;
        // Используем индекс IX_Trades_Symbol_TradeTime
        const string sql = @"
            SELECT * 
            FROM public.""Trades""
            WHERE ""Symbol"" = @Symbol 
            ORDER BY ""TradeTime"" DESC
            LIMIT @Count";
        return await db.QueryAsync<Trade>(sql, new { Symbol = symbol, Count = count });
    }

    public async Task BulkInsertAsync(IEnumerable<Trade> trades)
    {
        var tradeList = trades.ToList();
        if (!tradeList.Any()) return;

        // 1. Создаем анонимный объект с параметрами.
        //    Имена свойств должны ТОЧНО совпадать с именами параметров в функции.
        var parameters = new
        {
            p_trade_ids = tradeList.Select(t => t.TradeId).ToArray(),
            p_symbols = tradeList.Select(t => t.Symbol).ToArray(),
            p_prices = tradeList.Select(t => t.Price).ToArray(),
            p_quantities = tradeList.Select(t => t.Quantity).ToArray(),
            p_quote_quantities = tradeList.Select(t => t.QuoteQuantity).ToArray(),
            p_trade_times = tradeList.Select(t => t.TradeTime).ToArray(),
            p_is_buyer_makers = tradeList.Select(t => t.IsBuyerMaker).ToArray(),
            p_is_best_matches = tradeList.Select(t => t.IsBestMatch).ToArray()
        };

        // 2. Формируем SQL-запрос, который ВЫЗЫВАЕТ функцию через SELECT.
        const string sql = "SELECT public.sp_bulk_insert_trades(" +
                           "@p_trade_ids, @p_symbols, @p_prices, @p_quantities, " +
                           "@p_quote_quantities, @p_trade_times, @p_is_buyer_makers, @p_is_best_matches)";

        using var db = Connection;

        // 3. Выполняем как обычный ТЕКСТОВЫЙ запрос.
        await db.ExecuteAsync(sql, parameters);
    }

    public async Task<long?> GetLastTradeTimeAsync(string symbol)
    {
        using var db = Connection;
        const string sql = "SELECT MAX(\"TradeTime\") FROM \"Trades\" WHERE \"Symbol\" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<long?>(sql, new { Symbol = symbol });
    }

    public async Task ExecuteAggregationAsync()
    {
        using var db = Connection;

        // 1. Формируем SQL-запрос, который ВЫЗЫВАЕТ функцию через SELECT.
        const string sql = "SELECT public.sp_aggregate_trades_to_ohlcv()";

        // Увеличиваем таймаут, так как агрегация может быть долгой
        // 2. Выполняем как обычный ТЕКСТОВЫЙ запрос.
        await db.ExecuteAsync(sql, commandTimeout: 120);
    }
}
