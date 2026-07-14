using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class FeatureRepository : IFeatureRepository
{
    private readonly string _connectionString;

    public FeatureRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
               ?? throw new InvalidOperationException("Connection string not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task UpsertFeaturesAsync(IEnumerable<FeatureData> features)
    {
        var featureList = features.ToList();
        if (!featureList.Any())
        {
            return;
        }

        // 1. Формируем SQL-запрос, который ВЫЗЫВАЕТ функцию через SELECT.
        const string sql = @"
        SELECT public.sp_upsert_ohlcv_features(
            @p_symbols, @p_open_times, @p_rsi_14, @p_macd_signals, @p_macd_hists, @p_cvds
        )";

        // 2. Создаем параметры. Имена свойств должны совпадать с именами в SQL-строке.
        var parameters = new
        {
            p_symbols = featureList.Select(f => f.Symbol).ToArray(),
            p_open_times = featureList.Select(f => f.OpenTime).ToArray(),
            p_rsi_14 = featureList.Select(f => f.Rsi14).ToArray(),
            p_macd_signals = featureList.Select(f => f.MacdSignal).ToArray(),
            p_macd_hists = featureList.Select(f => f.MacdHist).ToArray(),
            p_cvds = featureList.Select(f => f.Cvd).ToArray()
        };

        using var db = Connection;

        // 3. Выполняем как обычный ТЕКСТОВЫЙ запрос.
        await db.ExecuteAsync(sql, parameters, commandTimeout: 120);
    }

    public async Task<long?> GetLastFeatureTimeAsync(string symbol)
    {
        using var db = Connection;
        const string sql = "SELECT MAX(\"OpenTime\") FROM public.\"Ohlcv_Features\" WHERE \"Symbol\" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<long?>(sql, new { Symbol = symbol });
    }
}
