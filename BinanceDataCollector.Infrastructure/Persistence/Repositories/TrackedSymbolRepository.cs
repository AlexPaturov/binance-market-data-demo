using BinanceDataCollector.Application.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

/// <summary>
/// Для отслеживания активности топ-X пар
/// </summary>
public class TrackedSymbolRepository : ITrackedSymbolRepository
{
    private readonly string _connectionString;

    public TrackedSymbolRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string not found.");
    }

    // Создаем новое подключение для каждого вызова
    private IDbConnection Connection => new SqlConnection(_connectionString);

    public async Task<IEnumerable<string>> GetActiveSymbolsAsync()
    {
        using var db = Connection;
        const string sql = "SELECT Symbol FROM dbo.TrackedSymbols WHERE IsActive = 1";
        return await db.QueryAsync<string>(sql);
    }

    // Очень важный метод, который обновит список одной транзакцией
    public async Task UpdateSymbolListAsync(IEnumerable<string> latestTopSymbols)
    {
        // Создаем DataTable для передачи в хранимую процедуру
        var symbolDt = new DataTable();
        symbolDt.Columns.Add("Symbol", typeof(string));
        foreach (var symbol in latestTopSymbols)
        {
            symbolDt.Rows.Add(symbol);
        }

        using var db = Connection;
        await db.ExecuteAsync(
            "dbo.sp_UpdateTrackedSymbols", // Имя новой хранимой процедуры
            new { Symbols = symbolDt.AsTableValuedParameter("dbo.SymbolListType") },
            commandType: CommandType.StoredProcedure
        );
    }
}
