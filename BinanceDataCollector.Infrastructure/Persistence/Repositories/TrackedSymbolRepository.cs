using BinanceDataCollector.Application.Interfaces;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class TrackedSymbolRepository : ITrackedSymbolRepository
{
    private readonly string _connectionString;

    public TrackedSymbolRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
                        ?? throw new InvalidOperationException("Connection string not found.");
    }

    // Создаем новое подключение для каждого вызова
    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task<IEnumerable<string>> GetActiveSymbolsAsync()
    {
        using var db = Connection;
        const string sql = "SELECT \"Symbol\" FROM public.\"TrackedSymbols\" WHERE \"IsActive\" = true";
        var tmp = await db.QueryAsync<string>(sql);
        return tmp;
    }

    public async Task UpdateSymbolListAsync(IEnumerable<string> latestTopSymbols)
    {
        using var db = Connection;

        // 1. Формируем SQL-запрос, который ВЫЗЫВАЕТ функцию через SELECT.
        //    Имя параметра в SQL-строке (@p_symbols) должно совпадать
        //    с именем свойства в анонимном объекте.
        const string sql = "SELECT public.sp_update_tracked_symbols(@p_symbols)";

        // 2. Создаем параметры. Имя свойства ДОЛЖНО СОВПАДАТЬ с именем в строке SQL.
        var parameters = new { p_symbols = latestTopSymbols.ToArray() };

        // 3. Выполняем как обычный ТЕКСТОВЫЙ запрос.
        //    Мы НЕ используем CommandType.StoredProcedure.
        await db.ExecuteAsync(sql, parameters);
    }
}
