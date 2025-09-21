using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class OhlcvRepository : IOhlcvRepository
{
    private readonly string _connectionString;

    public OhlcvRepository(IConfiguration configureOptions)
    {
        _connectionString = configureOptions.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Connection string not found.");
    }
    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    public async Task<IEnumerable<Ohlcv>> ClaimNewKlinesForProcessingAsync(int batchSize)
    {
        using var db = Connection;
        const string sql = @"
            WITH candidates AS (
                -- 1. Находим кандидатов для обработки
                SELECT ""Symbol"", ""OpenTime""
                FROM public.""Ohlcv_1min""
                WHERE ""ProcessingStatus"" = 'new'
                ORDER BY ""OpenTime"" ASC
                LIMIT @BatchSize
                -- Блокируем строки, чтобы другой воркер их не тронул
                FOR UPDATE SKIP LOCKED 
            ),
            updated AS (
                -- 2. Атомарно обновляем их статус на 'processing'
                UPDATE public.""Ohlcv_1min""
                SET ""ProcessingStatus"" = 'processing'
                WHERE (""Symbol"", ""OpenTime"") IN (SELECT ""Symbol"", ""OpenTime"" FROM candidates)
                -- 3. Возвращаем обновленные строки
                RETURNING *
            )
            SELECT * FROM updated ORDER BY ""OpenTime"" ASC;
        ";

        return await db.QueryAsync<Ohlcv>(sql, new { BatchSize = batchSize }, commandTimeout: 300);
    }

    public async Task MarkKlinesAsProcessedAsync(IEnumerable<long> openTimes)
    {
        using var db = Connection;
        const string sql = @"UPDATE public.""Ohlcv_1min"" SET ""ProcessingStatus"" = 'processed' 
                         WHERE ""OpenTime"" = ANY(@OpenTimes)";
        await db.ExecuteAsync(sql, new { OpenTimes = openTimes.ToList() });
    }

    public async Task<IEnumerable<Ohlcv>> GetWarmupKlinesAsync(string symbol, long beforeTime, int limit)
    {
        using var db = Connection;

        // Этот SQL-запрос выбирает 'limit' свечей для указанного символа,
        // которые произошли до указанного времени, и сортирует их по убыванию,
        // чтобы получить самые последние из "старых".
        const string sql = @"
        SELECT * 
        FROM public.""Ohlcv_1min"" 
        WHERE ""Symbol"" = @Symbol AND ""OpenTime"" < @BeforeTime 
        ORDER BY ""OpenTime"" DESC 
        LIMIT @Limit";

        return await db.QueryAsync<Ohlcv>(sql, new { Symbol = symbol, BeforeTime = beforeTime, Limit = limit });
    }

    public async Task<long?> GetLastKlineOpenTimeAsync(string symbol)
    {
        using var db = Connection;
        const string sql = @"SELECT MAX(""OpenTime"") FROM public.""Ohlcv_1min"" WHERE ""Symbol"" = @Symbol";
        return await db.QuerySingleOrDefaultAsync<long?>(sql, new { Symbol = symbol });
    }

    /// <summary>
    /// Выполняет массовую вставку или обновление (UPSERT) свечей в таблицу Ohlcv_1min.
    /// </summary>
    /// <remarks>
    /// Этот метод использует высокопроизводительную команду PostgreSQL UNNEST для "разворачивания"
    /// массивов данных в строки, а затем INSERT ... ON CONFLICT для атомарной вставки или обновления.
    /// Это гораздо быстрее, чем вставлять записи по одной.
    /// 
    /// Логика ON CONFLICT:
    /// - Если свечи с таким ("Symbol", "OpenTime") еще нет, она просто вставляется.
    /// - Если свеча уже существует, она ОБНОВЛЯЕТСЯ. Это важно для "текущей", еще не закрытой свечи.
    ///   - HighPrice обновляется на максимальное значение между старым и новым.
    ///   - LowPrice обновляется на минимальное значение.
    ///   - ClosePrice всегда перезаписывается новым значением.
    ///   - Volume СУММИРУЕТСЯ со старым значением.
    /// </remarks>
    /// <param name="klines">Коллекция свечей для сохранения.</param>
    public async Task BulkUpsertAsync(IEnumerable<Ohlcv> klines)
    {
        var klineList = klines.ToList();
        if (!klineList.Any())
        {
            return; // Ничего не делаем, если список пуст
        }

        const string sql = @"
            INSERT INTO public.""Ohlcv_1min"" (
                ""Symbol"", 
                ""OpenTime"", 
                ""OpenPrice"", 
                ""HighPrice"", 
                ""LowPrice"", 
                ""ClosePrice"", 
                ""Volume"", 
                ""ProcessingStatus"" -- Важно установить статус 'new' для новых свечей
            )
            SELECT 
                p_symbol, 
                p_open_time, 
                p_open_price, 
                p_high_price, 
                p_low_price, 
                p_close_price, 
                p_volume,
                'new' -- Новые/обновленные свечи готовы для расчета индикаторов
            FROM UNNEST(
                @Symbols, @OpenTimes, @OpenPrices, @HighPrices, 
                @LowPrices, @ClosePrices, @Volumes
            ) AS t(
                p_symbol, p_open_time, p_open_price, p_high_price, 
                p_low_price, p_close_price, p_volume
            )
            ON CONFLICT (""Symbol"", ""OpenTime"") DO UPDATE 
            SET
                ""HighPrice"" = GREATEST(public.""Ohlcv_1min"".""HighPrice"", EXCLUDED.""HighPrice""),
                ""LowPrice"" = LEAST(public.""Ohlcv_1min"".""LowPrice"", EXCLUDED.""LowPrice""),
                ""ClosePrice"" = EXCLUDED.""ClosePrice"",
                ""Volume"" = public.""Ohlcv_1min"".""Volume"" + EXCLUDED.""Volume"",
                ""ProcessingStatus"" = 'new';
        ";

        using var db = Connection;
        await db.ExecuteAsync(sql, new
        {
            Symbols = klineList.Select(k => k.Symbol).ToArray(),
            OpenTimes = klineList.Select(k => k.OpenTime).ToArray(),
            OpenPrices = klineList.Select(k => k.OpenPrice).ToArray(),
            HighPrices = klineList.Select(k => k.HighPrice).ToArray(),
            LowPrices = klineList.Select(k => k.LowPrice).ToArray(),
            ClosePrices = klineList.Select(k => k.ClosePrice).ToArray(),
            Volumes = klineList.Select(k => k.Volume).ToArray()
        });
    }
}
