using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly string _connectionString;

    // Инжектируем IConfiguration, чтобы получить строку подключения
    public TradeRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string not found.");
    }

    // Создаем новое подключение для каждого вызова
    private IDbConnection Connection => new SqlConnection(_connectionString);

    public async Task<Trade?> GetByIdAsync(long tradeId, string symbol)
    {
        using var db = Connection;
        const string sql = "SELECT * FROM Trades WHERE TradeId = @TradeId AND Symbol = @Symbol";
        return await db.QuerySingleOrDefaultAsync<Trade>(sql, new { TradeId = tradeId, Symbol = symbol });
    }

    public async Task<IEnumerable<Trade>> GetLatestTradesAsync(string symbol, int count)
    {
        using var db = Connection;
        // Используем индекс IX_Trades_Symbol_TradeTime
        const string sql = @"
            SELECT TOP (@Count) * 
            FROM Trades 
            WHERE Symbol = @Symbol 
            ORDER BY TradeTime DESC";
        return await db.QueryAsync<Trade>(sql, new { Symbol = symbol, Count = count });
    }

    // РЕАЛИЗАЦИЯ МАССОВОЙ ВСТАВКИ (КЛЮЧЕВОЙ МОМЕНТ)
    // Этот метод неэффективен для тысяч записей, см. следующий раздел для правильной реализации
    //public async Task BulkInsertAsync(IEnumerable<Trade> trades)
    //{
    //    using var db = Connection;
    //    const string sql = @"
    //        INSERT INTO Trades (TradeId, Symbol, Price, Quantity, QuoteQuantity, TradeTime, IsBuyerMaker, IsBestMatch, OrderId, Commission, CommissionAsset, IsMyTrade)
    //        VALUES (@TradeId, @Symbol, @Price, @Quantity, @QuoteQuantity, @TradeTime, @IsBuyerMaker, @IsBestMatch, @OrderId, @Commission, @CommissionAsset, @IsMyTrade);";

    //    await db.ExecuteAsync(sql, trades); // Dapper сам пройдется по коллекции
    //}

    // ПРАВИЛЬНАЯ РЕАЛИЗАЦИЯ BULK INSERT через TVP (Table-Valued Parameter)
    public async Task BulkInsertAsync(IEnumerable<Trade> trades)
    {
        var tradesDataTable = new DataTable();
        tradesDataTable.Columns.Add("TradeId", typeof(long));
        tradesDataTable.Columns.Add("Symbol", typeof(string));
        tradesDataTable.Columns.Add("Price", typeof(decimal));
        tradesDataTable.Columns.Add("Quantity", typeof(decimal));
        tradesDataTable.Columns.Add("QuoteQuantity", typeof(decimal));
        tradesDataTable.Columns.Add("TradeTime", typeof(long));
        tradesDataTable.Columns.Add("IsBuyerMaker", typeof(bool));
        tradesDataTable.Columns.Add("IsBestMatch", typeof(bool));
        tradesDataTable.Columns.Add("OrderId", typeof(long));
        tradesDataTable.Columns.Add("Commission", typeof(decimal));
        tradesDataTable.Columns.Add("CommissionAsset", typeof(string));
        tradesDataTable.Columns.Add("IsMyTrade", typeof(bool));

        // Добавляем nullable-поля, чтобы избежать ошибок с DBNull
        tradesDataTable.Columns["OrderId"].AllowDBNull = true;
        tradesDataTable.Columns["Commission"].AllowDBNull = true;
        tradesDataTable.Columns["CommissionAsset"].AllowDBNull = true;

        foreach (var trade in trades)
        {
            // Используем DBNull.Value для nullable-типов
            tradesDataTable.Rows.Add(
                trade.TradeId,
                trade.Symbol,
                trade.Price,
                trade.Quantity,
                trade.QuoteQuantity,
                trade.TradeTime,
                trade.IsBuyerMaker,
                trade.IsBestMatch,
                (object?)trade.OrderId ?? DBNull.Value,
                (object?)trade.Commission ?? DBNull.Value,
                (object?)trade.CommissionAsset ?? DBNull.Value,
                trade.IsMyTrade
            );
        }

        using var db = Connection;
        await db.ExecuteAsync(
            "dbo.sp_BulkInsertTrades", // Имя хранимой процедуры
            new { Trades = tradesDataTable.AsTableValuedParameter("dbo.TradeType") }, // Передаем DataTable как TVP
            commandType: CommandType.StoredProcedure
        );
    }

}
