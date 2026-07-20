using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

namespace BinanceDataCollector.Infrastructure.Persistence.Repositories;

public class DataQualityRepository : IDataQualityRepository
{
    private const int CheckTimeoutSeconds = 1800;

    private readonly string _connectionString;

    public DataQualityRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private IDbConnection Connection => new NpgsqlConnection(_connectionString);

    // ================================================================
    //  Месячные отчёты по сырым тикам
    // ================================================================

    public async Task<DataQualityReport> CheckSymbolMonthAsync(string symbol, int year, int month)
    {
        var periodStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = periodStart.AddMonths(1);
        var fromMs = new DateTimeOffset(periodStart).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(periodEnd).ToUnixTimeMilliseconds();

        using var db = Connection;
        // Тот же однопроходный подсчёт дефектов, что и в RunTradesChecksAsync (журнал за
        // период) — единый источник FILTER-логики. Метки из будущего в помесячный отчёт не входят.
        var d = await CountTradeDefectsAsync(db, symbol, fromMs, toMs);

        var status = d.Total == 0 || d.Invalid > 0 ? "error"
                   : d.Gaps > 0 || d.Outliers > 0   ? "warning"
                   : "ok";

        return new DataQualityReport
        {
            Symbol            = symbol,
            PeriodMonth       = periodStart,
            TradeCount        = d.Total,
            GapCount          = (int)d.Gaps,
            InvalidPriceCount = (int)d.Invalid,
            OutlierCount      = (int)d.Outliers,
            Status            = status,
            CheckedAt         = DateTime.UtcNow
        };
    }

    // Единый однопроходный подсчёт дефектов сделок в окне [fromMs, toMs): разрывы TradeId,
    // невалидные цена/объём, 5σ-выбросы, метки из будущего. Оконные функции и статистика —
    // в пределах символа (PARTITION/GROUP BY Symbol). Один источник FILTER-логики для
    // помесячного отчёта (CheckSymbolMonthAsync) и журнала за период (RunTradesChecksAsync).
    private async Task<(long Total, long Gaps, long Invalid, long Outliers, long Future)>
        CountTradeDefectsAsync(IDbConnection db, string? symbol, long fromMs, long toMs)
    {
        const string sql = @"
            WITH ordered AS (
                SELECT
                    ""Symbol"", ""TradeId"", ""Price"", ""Quantity"", ""TradeTime"",
                    LAG(""TradeId"") OVER (PARTITION BY ""Symbol"" ORDER BY ""TradeId"") AS prev_id
                FROM public.""Trades""
                WHERE ""TradeTime"" >= @FromMs AND ""TradeTime"" < @ToMs
                  AND (@Symbol::varchar IS NULL OR ""Symbol"" = @Symbol)
            ),
            stats AS (
                SELECT ""Symbol"", AVG(""Price"") AS avg_p, STDDEV(""Price"") AS std_p
                FROM ordered GROUP BY ""Symbol""
            )
            SELECT
                COUNT(*)                                                                        AS total,
                COUNT(*) FILTER (WHERE o.prev_id IS NOT NULL AND o.""TradeId"" > o.prev_id + 1) AS gaps,
                COUNT(*) FILTER (WHERE o.""Price"" <= 0 OR o.""Quantity"" <= 0)                 AS invalid,
                COUNT(*) FILTER (WHERE s.std_p > 0 AND ABS(o.""Price"" - s.avg_p) > 5 * s.std_p) AS outliers,
                COUNT(*) FILTER (WHERE o.""TradeTime"" > @NowMs)                                AS future
            FROM ordered o
            JOIN stats s ON s.""Symbol"" = o.""Symbol"";";

        return await db.QuerySingleAsync<(long Total, long Gaps, long Invalid, long Outliers, long Future)>(
            sql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol, NowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            commandTimeout: CheckTimeoutSeconds);
    }

    public async Task UpsertReportAsync(DataQualityReport report)
    {
        const string sql = @"
            SELECT public.sp_ensure_month_partitions(
                (EXTRACT(EPOCH FROM @PeriodMonth::timestamptz) * 1000)::bigint);

            INSERT INTO public.""DataQualityReports""
                (""Symbol"", ""PeriodMonth"", ""TradeCount"", ""GapCount"", ""InvalidPriceCount"", ""OutlierCount"", ""Status"", ""CheckedAt"")
            VALUES
                (@Symbol, @PeriodMonth, @TradeCount, @GapCount, @InvalidPriceCount, @OutlierCount, @Status, @CheckedAt)
            ON CONFLICT (""Symbol"", ""PeriodMonth"") DO UPDATE SET
                ""TradeCount""        = EXCLUDED.""TradeCount"",
                ""GapCount""          = EXCLUDED.""GapCount"",
                ""InvalidPriceCount"" = EXCLUDED.""InvalidPriceCount"",
                ""OutlierCount""      = EXCLUDED.""OutlierCount"",
                ""Status""            = EXCLUDED.""Status"",
                ""CheckedAt""         = EXCLUDED.""CheckedAt"";";

        using var db = Connection;
        await db.ExecuteAsync(sql, report);
    }

    public async Task<IEnumerable<DateTime>> GetUncheckedMonthsAsync()
    {
        // Find distinct months present in Trades partitions that have no DataQualityReport yet.
        const string sql = @"
            WITH partitions AS (
                SELECT inhrelid::regclass::text AS part_name,
                       pg_total_relation_size(inhrelid) AS size_bytes
                FROM pg_inherits
                WHERE inhparent = 'public.""Trades""'::regclass
            ),
            months AS (
                SELECT TO_DATE(
                    REGEXP_REPLACE(part_name, '.*Trades_(\d{4})_(\d{2}).*', '\1-\2-01'),
                    'YYYY-MM-DD'
                ) AS period_month
                FROM partitions
                WHERE size_bytes > 1048576  -- skip empty partitions (< 1MB)
                  AND part_name ~ 'Trades_\d{4}_\d{2}'
            )
            SELECT m.period_month
            FROM months m
            LEFT JOIN public.""DataQualityReports"" r ON r.""PeriodMonth"" = m.period_month
            WHERE r.""PeriodMonth"" IS NULL
            ORDER BY m.period_month;";

        using var db = Connection;
        return await db.QueryAsync<DateTime>(sql);
    }

    public async Task<IEnumerable<DataQualityReport>> GetReportsAsync(string? symbol = null, string? status = null)
    {
        var where = new List<string>();
        if (symbol is not null) where.Add(@"""Symbol"" = @Symbol");
        if (status is not null) where.Add(@"""Status"" = @Status");
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        var sql = $@"
            SELECT * FROM public.""DataQualityReports""
            {whereClause}
            ORDER BY ""PeriodMonth"" DESC, ""Symbol"" ASC;";

        using var db = Connection;
        return await db.QueryAsync<DataQualityReport>(sql, new { Symbol = symbol, Status = status });
    }

    // ================================================================
    //  Проверки, запускаемые вручную
    // ================================================================

    public async Task<IReadOnlyList<DataQualityFinding>> RunTradesChecksAsync(string? symbol, DateTime from, DateTime to)
    {
        var (fromMs, toMs) = ValidateRange(from, to);
        var findings = new List<DataQualityFinding>();

        using var db = Connection;

        // Однопроходный подсчёт дефектов — единый с CheckSymbolMonthAsync (помесячный отчёт).
        var main = await CountTradeDefectsAsync(db, symbol, fromMs, toMs);

        findings.Add(Finding(DataQualityChecks.GroupTrades, "trade_count", symbol, from, to,
            main.Total == 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk,
            main.Total, $"{{\"trades\": {main.Total}}}"));

        findings.Add(Finding(DataQualityChecks.GroupTrades, "trade_id_gaps", symbol, from, to,
            main.Gaps > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, main.Gaps));

        findings.Add(Finding(DataQualityChecks.GroupTrades, "invalid_price_or_quantity", symbol, from, to,
            main.Invalid > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, main.Invalid));

        findings.Add(Finding(DataQualityChecks.GroupTrades, "price_outliers_5sigma", symbol, from, to,
            main.Outliers > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, main.Outliers));

        findings.Add(Finding(DataQualityChecks.GroupTrades, "trade_time_in_future", symbol, from, to,
            main.Future > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, main.Future));

        // Дубликат: та же сделка (TradeId, Symbol) с разным TradeTime. PK — тройка,
        // поэтому такие строки существуют легально и дедупом не отсекаются.
        const string dupSql = @"
            SELECT COUNT(*) FROM (
                SELECT ""TradeId"", ""Symbol""
                FROM public.""Trades""
                WHERE ""TradeTime"" >= @FromMs AND ""TradeTime"" < @ToMs
                  AND (@Symbol::varchar IS NULL OR ""Symbol"" = @Symbol)
                GROUP BY ""TradeId"", ""Symbol""
                HAVING COUNT(DISTINCT ""TradeTime"") > 1
            ) d;";

        var duplicates = await db.ExecuteScalarAsync<long>(dupSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupTrades, "duplicate_trade_id", symbol, from, to,
            duplicates > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, duplicates));

        // Сделки по символам, которых нет в TrackedSymbols. FK нет — связь только логическая.
        const string untrackedSql = @"
            SELECT COALESCE(json_agg(x.""Symbol""), '[]')::text AS symbols, COUNT(*) AS cnt
            FROM (
                SELECT DISTINCT t.""Symbol""
                FROM public.""Trades"" t
                LEFT JOIN public.""TrackedSymbols"" ts ON ts.""Symbol"" = t.""Symbol""
                WHERE t.""TradeTime"" >= @FromMs AND t.""TradeTime"" < @ToMs
                  AND (@Symbol::varchar IS NULL OR t.""Symbol"" = @Symbol)
                  AND ts.""Symbol"" IS NULL
            ) x;";

        var untracked = await db.QuerySingleAsync<(string Symbols, long Cnt)>(untrackedSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupTrades, "untracked_symbol", symbol, from, to,
            untracked.Cnt > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk,
            untracked.Cnt, untracked.Cnt > 0 ? $"{{\"symbols\": {untracked.Symbols}}}" : null));

        return findings;
    }

    public async Task<IReadOnlyList<DataQualityFinding>> RunOhlcvChecksAsync(string? symbol, DateTime from, DateTime to)
    {
        var (fromMs, toMs) = ValidateRange(from, to);
        var findings = new List<DataQualityFinding>();

        using var db = Connection;

        // Структурные инварианты свечи. CHECK-констрейнтов на таблице нет,
        // поэтому нарушения физически возможны и ловятся только здесь.
        const string invariantSql = @"
            SELECT
                COUNT(*)                                                                    AS total,
                COUNT(*) FILTER (WHERE ""HighPrice"" < ""LowPrice"")                        AS high_lt_low,
                -- Только свечи с корректным диапазоном: при High < Low условие BETWEEN
                -- ложно для любого Open/Close, и один дефект посчитался бы дважды.
                COUNT(*) FILTER (WHERE ""HighPrice"" >= ""LowPrice""
                                   AND (""OpenPrice""  NOT BETWEEN ""LowPrice"" AND ""HighPrice""
                                     OR ""ClosePrice"" NOT BETWEEN ""LowPrice"" AND ""HighPrice"")) AS oc_outside,
                COUNT(*) FILTER (WHERE ""OpenTime"" % 60000 <> 0)                           AS misaligned,
                COUNT(*) FILTER (WHERE ""Volume"" < 0)                                      AS negative_volume
            FROM public.""Ohlcv_1min""
            WHERE ""OpenTime"" >= @FromMs AND ""OpenTime"" < @ToMs
              AND (@Symbol::varchar IS NULL OR ""Symbol"" = @Symbol);";

        var inv = await db.QuerySingleAsync<(long Total, long HighLtLow, long OcOutside, long Misaligned, long NegVol)>(
            invariantSql, new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "candle_count", symbol, from, to,
            DataQualityChecks.SeverityOk, inv.Total, $"{{\"candles\": {inv.Total}}}"));

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "high_below_low", symbol, from, to,
            inv.HighLtLow > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, inv.HighLtLow));

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "open_close_outside_range", symbol, from, to,
            inv.OcOutside > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, inv.OcOutside));

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "opentime_not_minute_aligned", symbol, from, to,
            inv.Misaligned > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, inv.Misaligned));

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "negative_volume", symbol, from, to,
            inv.NegVol > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, inv.NegVol));

        // Пропущенные минуты: разрыв в последовательности OpenTime с шагом 60000.
        const string missingSql = @"
            WITH ordered AS (
                SELECT ""Symbol"", ""OpenTime"",
                       LAG(""OpenTime"") OVER (PARTITION BY ""Symbol"" ORDER BY ""OpenTime"") AS prev_time
                FROM public.""Ohlcv_1min""
                WHERE ""OpenTime"" >= @FromMs AND ""OpenTime"" < @ToMs
                  AND (@Symbol::varchar IS NULL OR ""Symbol"" = @Symbol)
            )
            SELECT COALESCE(SUM((""OpenTime"" - prev_time) / 60000 - 1), 0)
            FROM ordered
            WHERE prev_time IS NOT NULL AND ""OpenTime"" - prev_time > 60000;";

        var missingMinutes = await db.ExecuteScalarAsync<long>(missingSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "missing_minutes", symbol, from, to,
            missingMinutes > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, missingMinutes));

        // Свеча с нулевым объёмом, хотя тики за эту минуту есть — признак сбоя агрегации.
        const string zeroVolSql = @"
            SELECT COUNT(*)
            FROM public.""Ohlcv_1min"" c
            WHERE c.""OpenTime"" >= @FromMs AND c.""OpenTime"" < @ToMs
              AND (@Symbol::varchar IS NULL OR c.""Symbol"" = @Symbol)
              AND c.""Volume"" = 0
              AND EXISTS (
                  SELECT 1 FROM public.""Trades"" t
                  WHERE t.""Symbol"" = c.""Symbol""
                    AND t.""TradeTime"" >= c.""OpenTime""
                    AND t.""TradeTime"" <  c.""OpenTime"" + 60000
              );";

        var zeroVolume = await db.ExecuteScalarAsync<long>(zeroVolSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupOhlcv, "zero_volume_with_trades", symbol, from, to,
            zeroVolume > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, zeroVolume));

        return findings;
    }

    public async Task<IReadOnlyList<DataQualityFinding>> RunFeaturesChecksAsync(string? symbol, DateTime from, DateTime to)
    {
        var (fromMs, toMs) = ValidateRange(from, to);
        var findings = new List<DataQualityFinding>();

        using var db = Connection;

        const string rsiSql = @"
            SELECT
                COUNT(*)                                                       AS total,
                COUNT(*) FILTER (WHERE ""RSI_14"" < 0 OR ""RSI_14"" > 100)     AS rsi_bad
            FROM public.""Ohlcv_Features""
            WHERE ""OpenTime"" >= @FromMs AND ""OpenTime"" < @ToMs
              AND (@Symbol::varchar IS NULL OR ""Symbol"" = @Symbol);";

        var rsi = await db.QuerySingleAsync<(long Total, long RsiBad)>(rsiSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupFeatures, "feature_count", symbol, from, to,
            DataQualityChecks.SeverityOk, rsi.Total, $"{{\"features\": {rsi.Total}}}"));

        findings.Add(Finding(DataQualityChecks.GroupFeatures, "rsi_out_of_range", symbol, from, to,
            rsi.RsiBad > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, rsi.RsiBad));

        // Свеча помечена 'processed', но индикаторов для неё нет — тихая потеря
        // на границе feature-pipeline (FK между таблицами не объявлен).
        const string missingFeaturesSql = @"
            SELECT COUNT(*)
            FROM public.""Ohlcv_1min"" c
            LEFT JOIN public.""Ohlcv_Features"" f
                   ON f.""Symbol"" = c.""Symbol"" AND f.""OpenTime"" = c.""OpenTime""
            WHERE c.""OpenTime"" >= @FromMs AND c.""OpenTime"" < @ToMs
              AND (@Symbol::varchar IS NULL OR c.""Symbol"" = @Symbol)
              AND c.""ProcessingStatus"" = 'processed'
              AND f.""Symbol"" IS NULL;";

        var missingFeatures = await db.ExecuteScalarAsync<long>(missingFeaturesSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupFeatures, "processed_candle_without_features", symbol, from, to,
            missingFeatures > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk, missingFeatures));

        // Индикаторы без соответствующей свечи.
        const string orphanSql = @"
            SELECT COUNT(*)
            FROM public.""Ohlcv_Features"" f
            LEFT JOIN public.""Ohlcv_1min"" c
                   ON c.""Symbol"" = f.""Symbol"" AND c.""OpenTime"" = f.""OpenTime""
            WHERE f.""OpenTime"" >= @FromMs AND f.""OpenTime"" < @ToMs
              AND (@Symbol::varchar IS NULL OR f.""Symbol"" = @Symbol)
              AND c.""Symbol"" IS NULL;";

        var orphans = await db.ExecuteScalarAsync<long>(orphanSql,
            new { FromMs = fromMs, ToMs = toMs, Symbol = symbol }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupFeatures, "orphan_features", symbol, from, to,
            orphans > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, orphans));

        return findings;
    }

    public async Task<IReadOnlyList<DataQualityFinding>> RunPipelineChecksAsync()
    {
        var findings = new List<DataQualityFinding>();
        var now = DateTime.UtcNow;

        using var db = Connection;

        // 1. Watermark обогнал данные. Выборка идёт по '>= watermark', поэтому всё,
        //    что осталось позади отметки, выпадает из обработки НАВСЕГДА и молча.
        const string aheadSql = @"
            SELECT
                w.""ProcessName"",
                w.""LastProcessedTimestamp"" AS watermark,
                CASE w.""ProcessName""
                    WHEN 'OhlcvAggregator'   THEN (SELECT COALESCE(MAX(""TradeTime""), 0) FROM public.""Trades"")
                    WHEN 'FeatureCalculator' THEN (SELECT COALESCE(MAX(""OpenTime""), 0)  FROM public.""Ohlcv_1min"")
                    ELSE 0
                END AS max_source
            FROM public.""Processing_Watermarks"" w;";

        var watermarks = (await db.QueryAsync<(string ProcessName, long Watermark, long MaxSource)>(
            aheadSql, commandTimeout: CheckTimeoutSeconds)).ToList();

        var ahead = watermarks.Where(w => w.MaxSource > 0 && w.Watermark > w.MaxSource).ToList();
        findings.Add(Finding(DataQualityChecks.GroupPipeline, "watermark_ahead_of_data", null, now, now,
            ahead.Count > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk,
            ahead.Count,
            ahead.Count > 0
                ? "{\"processes\": [" + string.Join(",", ahead.Select(a =>
                    $"{{\"process\":\"{a.ProcessName}\",\"watermark\":{a.Watermark},\"maxSource\":{a.MaxSource}}}")) + "]}"
                : null));

        // 2. Отсутствует запись watermark'а для процесса, который на неё опирается.
        var expected = new[] { "OhlcvAggregator", "FeatureCalculator" };
        var missing = expected.Where(p => watermarks.All(w => w.ProcessName != p)).ToList();
        findings.Add(Finding(DataQualityChecks.GroupPipeline, "watermark_missing", null, now, now,
            missing.Count > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk,
            missing.Count,
            missing.Count > 0 ? $"{{\"processes\": [{string.Join(",", missing.Select(m => $"\"{m}\""))}]}}" : null));

        // 3. Watermark завис: не двигался больше часа, хотя необработанные записи есть.
        //    Для агрегатора «есть работа» — это непустая очередь грязных минут.
        const string stalledSql = @"
            SELECT COUNT(*)
            FROM public.""Processing_Watermarks"" w
            WHERE w.""LastUpdate_UTC"" < NOW() - INTERVAL '1 hour'
              AND (
                  (w.""ProcessName"" = 'OhlcvAggregator'
                   AND EXISTS (SELECT 1 FROM public.""DirtyMinutes""))
                  OR
                  (w.""ProcessName"" = 'FeatureCalculator'
                   AND EXISTS (SELECT 1 FROM public.""Ohlcv_1min"" WHERE ""ProcessingStatus"" = 'new'))
              );";

        var stalled = await db.ExecuteScalarAsync<long>(stalledSql, commandTimeout: CheckTimeoutSeconds);
        findings.Add(Finding(DataQualityChecks.GroupPipeline, "watermark_stalled", null, now, now,
            stalled > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, stalled));

        // 4. Символы, исчерпавшие лимит попыток аудита: по текущему запросу выборки
        //    (RetryCount < MaxRetries) они больше никогда не попадут в аудит.
        const string exhaustedSql = @"
            SELECT COALESCE(json_agg(""Symbol""), '[]')::text AS symbols, COUNT(*) AS cnt
            FROM public.""HistoricalAudit_Watermarks""
            WHERE ""Status"" = 'Failed' AND ""RetryCount"" >= @MaxRetries;";

        var exhausted = await db.QuerySingleAsync<(string Symbols, long Cnt)>(exhaustedSql,
            new { MaxRetries = 5 }, commandTimeout: CheckTimeoutSeconds);

        findings.Add(Finding(DataQualityChecks.GroupPipeline, "audit_retries_exhausted", null, now, now,
            exhausted.Cnt > 0 ? DataQualityChecks.SeverityError : DataQualityChecks.SeverityOk,
            exhausted.Cnt, exhausted.Cnt > 0 ? $"{{\"symbols\": {exhausted.Symbols}}}" : null));

        // 5. Символ деактивирован, но висит в аудите как Pending.
        const string inactiveSql = @"
            SELECT COUNT(*)
            FROM public.""HistoricalAudit_Watermarks"" w
            JOIN public.""TrackedSymbols"" ts ON ts.""Symbol"" = w.""Symbol""
            WHERE ts.""IsActive"" = false AND w.""Status"" = 'Pending';";

        var inactivePending = await db.ExecuteScalarAsync<long>(inactiveSql, commandTimeout: CheckTimeoutSeconds);
        findings.Add(Finding(DataQualityChecks.GroupPipeline, "inactive_symbol_pending_audit", null, now, now,
            inactivePending > 0 ? DataQualityChecks.SeverityWarning : DataQualityChecks.SeverityOk, inactivePending));

        return findings;
    }

    public async Task SaveFindingsAsync(IEnumerable<DataQualityFinding> findings)
    {
        var list = findings.ToList();
        if (list.Count == 0) return;

        // Партиция под месяц находки может ещё не существовать (проверка могла
        // затронуть месяц, в котором ещё ничего не писалось).
        const string sql = @"
            SELECT public.sp_ensure_month_partitions(
                (EXTRACT(EPOCH FROM @PeriodFrom) * 1000)::bigint);

            INSERT INTO public.""DataQualityFindings""
                (""CheckGroup"", ""CheckType"", ""Symbol"", ""PeriodFrom"", ""PeriodTo"",
                 ""Severity"", ""Count"", ""Details"", ""CheckedAt"")
            VALUES
                (@CheckGroup, @CheckType, @Symbol, @PeriodFrom, @PeriodTo,
                 @Severity, @Count, @Details::jsonb, @CheckedAt);";

        using var db = Connection;
        await db.ExecuteAsync(sql, list);
    }

    public async Task<IEnumerable<DataQualityFinding>> GetFindingsAsync(
        string? checkGroup = null, string? severity = null, string? symbol = null, int limit = 200)
    {
        var where = new List<string>();
        if (checkGroup is not null) where.Add(@"""CheckGroup"" = @CheckGroup");
        if (severity is not null) where.Add(@"""Severity"" = @Severity");
        if (symbol is not null) where.Add(@"""Symbol"" = @Symbol");
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        var sql = $@"
            SELECT ""Id"", ""CheckGroup"", ""CheckType"", ""Symbol"", ""PeriodFrom"", ""PeriodTo"",
                   ""Severity"", ""Count"", ""Details""::text AS ""Details"", ""CheckedAt""
            FROM public.""DataQualityFindings""
            {whereClause}
            ORDER BY ""CheckedAt"" DESC, ""Id"" DESC
            LIMIT @Limit;";

        using var db = Connection;
        return await db.QueryAsync<DataQualityFinding>(sql,
            new { CheckGroup = checkGroup, Severity = severity, Symbol = symbol, Limit = limit });
    }

    // ================================================================
    //  Вспомогательное
    // ================================================================

    /// <summary>
    /// Диапазон ограничен жёстко: без верхней границы любая проверка вырождается
    /// в полный скан "Trades" (сотни ГБ) и укладывает базу по I/O.
    /// </summary>
    private static (long FromMs, long ToMs) ValidateRange(DateTime from, DateTime to)
    {
        if (to <= from)
            throw new ArgumentException("Конец периода должен быть позже начала.");

        if (to - from > DataQualityChecks.MaxRange)
            throw new ArgumentException(
                $"Диапазон проверки не может превышать {DataQualityChecks.MaxRange.TotalDays:0} дней. " +
                $"Запрошено: {(to - from).TotalDays:0} дней.");

        return (new DateTimeOffset(from.ToUniversalTime()).ToUnixTimeMilliseconds(),
                new DateTimeOffset(to.ToUniversalTime()).ToUnixTimeMilliseconds());
    }

    private static DataQualityFinding Finding(
        string group, string type, string? symbol, DateTime from, DateTime to,
        string severity, long count, string? details = null) => new()
    {
        CheckGroup = group,
        CheckType  = type,
        Symbol     = symbol,
        PeriodFrom = from.ToUniversalTime(),
        PeriodTo   = to.ToUniversalTime(),
        Severity   = severity,
        Count      = count,
        Details    = details,
        CheckedAt  = DateTime.UtcNow
    };
}
