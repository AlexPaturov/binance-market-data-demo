using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<TradeRepository> _logger;

    public TradeRepository(
        IConfiguration configuration,
        ILogger<TradeRepository> logger
    )
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                        ?? throw new InvalidOperationException("Connection string not found.");
        _logger = logger;
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task<Trade?> GetTradeByIdAsync(long tradeId, string symbol)
    {
        using var db = Connection;

        const string sql = @"
            SELECT * 
            FROM public.""Trades"" 
            WHERE ""TradeId"" = @TradeId AND ""Symbol"" = @Symbol;
        ";

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
        return await db.QueryAsync<Trade>(sql, new { Symbol = symbol, Count = count }, commandTimeout: 120);
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
        await db.ExecuteAsync(sql, parameters, commandTimeout: 600);
    }

    public async Task<long?> GetLastTradeTimeAsync(string symbol)
    {
        using var db = Connection;
        const string sql = "SELECT MAX(\"TradeTime\") FROM \"Trades\" WHERE \"Symbol\" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<long?>(sql, new { Symbol = symbol }, commandTimeout: 120);
    }

    public async Task ExecuteAggregationAsync(long startTimestamp, long endTimestamp)
    {
        using var db = Connection;
        const string sql = "SELECT public.sp_aggregate_trades_to_ohlcv(@Start, @End)";
        // Даем процедуре достаточно времени на выполнение одной порции
        await db.ExecuteAsync(sql,
            new { Start = startTimestamp, End = endTimestamp },
            commandTimeout: 300); // 5 минут
    }

    // на уаление ?
    public async Task<long?> GetLastTradeIdAsync(string symbol)
    {
        using var db = Connection;
        const string sql = @"SELECT MAX(""TradeId"") FROM public.""Trades"" WHERE ""Symbol"" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<long?>(sql, new { Symbol = symbol }, commandTimeout: 120);
    }

    // список всех дыр для символа за 24 часа
    public async Task<List<DataGap>> GetGapsForSymbolDayAsync(string symbol)
    {
        const string sql = @"
            WITH OrderedTrades AS (
                SELECT
                    ""TradeId"",
                    -- Используем LAG() для получения ID предыдущей сделки
                    LAG(""TradeId"", 1) OVER (ORDER BY ""TradeId"" ASC) AS ""PrevTradeId""
                FROM public.""Trades""
                WHERE 
                    ""Symbol"" = @Symbol 
                    -- Фильтрация по TradeTime гораздо эффективнее, если по нему есть индекс.
                    -- Мы вычисляем временную метку 48 часов назад ОДИН РАЗ.
                    AND ""TradeTime"" >= (EXTRACT(EPOCH FROM (NOW() - INTERVAL '24 hours')) * 1000)::BIGINT
            )
            SELECT
                ""PrevTradeId"" + 1 AS ""GapStart"", -- Маппинг на record DataGap
                ""TradeId"" - 1 AS ""GapEnd""       -- Маппинг на record DataGap
            FROM OrderedTrades
            WHERE
                -- Дыра есть, если ID не идут подряд
                ""TradeId"" > ""PrevTradeId"" + 1
            ORDER BY 
                ""GapStart"" ASC; -- Сортируем по началу дыры
            ";
        using var db = Connection;
        var gaps = await db.QueryAsync<DataGap>(sql, new { Symbol = symbol }, commandTimeout: 120);
        return gaps.AsList();
    }

    public async Task<long?> GetLastTradeIdBeforeTimestampAsync(string symbol, long timestampMs)
    {
        using var db = Connection;

        // Этот запрос очень эффективно использует индекс IX_Trades_Symbol_TradeTime
        const string sql = @"
            SELECT ""TradeId""
            FROM public.""Trades""
            WHERE ""Symbol"" = @Symbol AND ""TradeTime"" < @TimestampMs
            ORDER BY ""TradeTime"" DESC, ""TradeId"" DESC
            LIMIT 1;
        ";

        return await db.QuerySingleOrDefaultAsync<long?>(sql, new
        {
            Symbol = symbol,
            TimestampMs = timestampMs
        }, commandTimeout: 120);
    }

    public async Task<Trade?> GetLastTradeAsync(string symbol)
    {
        using var db = Connection;

        // Этот запрос также эффективно использует индекс IX_Trades_Symbol_TradeTime
        const string sql = @"
            SELECT *
            FROM public.""Trades""
            WHERE ""Symbol"" = @Symbol
            ORDER BY ""TradeTime"" DESC, ""TradeId"" DESC
            LIMIT 1;
    ";
        return await db.QuerySingleOrDefaultAsync<Trade>(sql, new { Symbol = symbol }, commandTimeout: 120);
    }

    public async Task<IEnumerable<long>> GetTradeIdsInWindowAsync(string symbol, long startTradeId, long endTradeId)
    {
        // 1. Создаем Stopwatch для замера времени
        var stopwatch = Stopwatch.StartNew();
        using var db = Connection;

        const string sql = @"
        SELECT ""TradeId"" 
        FROM public.""Trades""
        WHERE ""Symbol"" = @Symbol 
          AND ""TradeId"" >= @StartTradeId 
          AND ""TradeId"" <= @EndTradeId
        ORDER BY ""TradeId"" ASC";

        try
        {
            return await db.QueryAsync<long>(sql, new
            {
                Symbol = symbol,
                StartTradeId = startTradeId,
                EndTradeId = endTradeId
            }, commandTimeout: 600);
        }
        catch (Exception ex)
        {
            // 2. Останавливаем Stopwatch в блоке catch
            stopwatch.Stop();

            // 3. Создаем новое, более информативное исключение
            var detailedException = new InvalidOperationException(
                $"Ошибка при поиске дыр для символа '{symbol}' в диапазоне ID {startTradeId}-{endTradeId} " +
                $"после {stopwatch.ElapsedMilliseconds} мс.",
                ex // <-- Вкладываем оригинальное исключение внутрь
            );

            // 4. Логируем ОРИГИНАЛЬНОЕ исключение со всеми деталями
            _logger.LogError(ex,
                "ЗАПРОС УПАЛ ПО ТАЙМАУТУ. Символ: {Symbol}, Диапазон ID: {StartId} - {EndId}, Время выполнения до сбоя: {Elapsed} мс.",
                symbol,
                startTradeId,
                endTradeId,
                stopwatch.ElapsedMilliseconds);

            // 5. Пробрасываем НОВОЕ, обогащенное исключение наверх.
            // Логика Hangfire по перезапуску не сломается.
            throw detailedException;
        }
    }

    public async Task<List<DataGap>> FindGapsInTimeWindowAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        using var db = Connection;
        var startTimeMs = new DateTimeOffset(startTime).ToUnixTimeMilliseconds();
        var endTimeMs = new DateTimeOffset(endTime).ToUnixTimeMilliseconds();

        // Этот запрос очень похож на наш старый, но он будет работать
        // с маленькими временными окнами, поэтому будет быстрым.
        const string sql = @"
        WITH OrderedTrades AS (
            SELECT
                ""TradeId"",
                LAG(""TradeId"", 1) OVER (ORDER BY ""TradeId"" ASC) AS ""PrevTradeId""
            FROM public.""Trades""
            WHERE 
                ""Symbol"" = @Symbol 
                AND ""TradeTime"" >= @StartTimeMs
                AND ""TradeTime"" < @EndTimeMs
        )
        SELECT
            ""PrevTradeId"" + 1 AS ""GapStart"",
            ""TradeId"" - 1 AS ""GapEnd""
        FROM OrderedTrades
        WHERE ""TradeId"" > ""PrevTradeId"" + 1;
    ";

        var gaps = await db.QueryAsync<DataGap>(sql, new
        {
            Symbol = symbol,
            StartTimeMs = startTimeMs,
            EndTimeMs = endTimeMs
        }, commandTimeout: 120);
        return gaps.AsList();
    }

    /// <summary>
    /// Находит минимальный и максимальный TradeId в указанном временном окне.
    /// </summary>
    /// <remarks>
    /// Этот запрос эффективно использует композитный индекс по ("Symbol", "TradeTime").
    /// Он быстро находит нужный временной диапазон и выполняет агрегацию
    /// только на этом небольшом подмножестве данных.
    /// </remarks>
    public async Task<(long? minId, long? maxId)> GetMinMaxTradeIdInWindowAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        using var db = Connection;
        var startTimeMs = new DateTimeOffset(startTime).ToUnixTimeMilliseconds();
        var endTimeMs = new DateTimeOffset(endTime).ToUnixTimeMilliseconds();

        const string sql = @"
            SELECT MIN(""TradeId""), MAX(""TradeId"")
            FROM public.""Trades""
            WHERE ""Symbol"" = @Symbol 
            AND ""TradeTime"" >= @StartTimeMs 
            AND ""TradeTime"" < @EndTimeMs";

        // Dapper умеет мапить результат в кортеж (tuple)
        return await db.QuerySingleOrDefaultAsync<(long?, long?)>(sql, new
        {
            Symbol = symbol,
            StartTimeMs = startTimeMs,
            EndTimeMs = endTimeMs
        });
    }

    public async Task<Trade?> GetLastTradeAsync()
    {
        using var db = Connection;
        const string sql = @"SELECT * FROM ""Trades"" ORDER BY ""TradeTime"" DESC LIMIT 1";
        return await db.QuerySingleOrDefaultAsync<Trade?>(sql, commandTimeout:120);
    }
}
