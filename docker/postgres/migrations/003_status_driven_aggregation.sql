-- Агрегация свечей от СТАТУСА тиков, а не от watermark'а по времени.
--
-- Проблема, которую это чинит:
--   Старый агрегатор шёл вперёд по времени окнами от watermark'а:
--       WHERE ProcessingStatus = 'new' AND TradeTime >= watermark AND TradeTime < watermark + окно
--   Как только watermark проходил момент T, любые тики с TradeTime < T, вставленные
--   позже, не агрегировались НИКОГДА — они навсегда оставались 'new'.
--
--   А вставляют старые тики штатные пути:
--     * CsvImportWorker — архивы импортируются джобами вразнобой (сегодня март, завтра январь);
--     * FillGapWorker   — закрытие дыр по определению вставляет старые тики;
--     * OnlineArchiveImportWorker — историческая дозагрузка.
--
--   То есть закрытая дыра не попадала в свечи: свеча оставалась посчитанной по неполным данным.
--
-- Решение:
--   Окно берётся не от watermark'а, а от САМОГО СТАРОГО необработанного тика.
--   Свеча пересчитывается ЦЕЛИКОМ из всех тиков минуты (а не мерджится инкрементально),
--   поэтому порядок прихода данных перестаёт иметь значение, а операция идемпотентна.
--   Пересчитанная свеча помечается 'new' — feature-pipeline пересчитает по ней индикаторы.
--
--   Watermark остаётся, но только как индикатор прогресса, а не как фильтр корректности.
--
--   psql -U bindatacoll -d market_analytics -f 003_status_driven_aggregation.sql

BEGIN;

-- ============================================================================
--  1. Индекс: искать самый старый необработанный тик
-- ============================================================================

-- Был частичный индекс по (ProcessingStatus) — по нему нельзя найти MIN(TradeTime)
-- среди необработанных. Нужен индекс по времени.
DROP INDEX IF EXISTS public.ix_trades_processingstatus;

-- (TradeTime, Symbol), а не только TradeTime: по нему берётся и самый старый
-- необработанный тик, и список символов окна — оба запроса index-only.
CREATE INDEX IF NOT EXISTS ix_trades_new_tradetime
    ON public."Trades" USING btree ("TradeTime", "Symbol")
    WHERE ("ProcessingStatus")::text = 'new'::text;

-- ============================================================================
--  2. Агрегация от статуса
-- ============================================================================

DROP FUNCTION IF EXISTS public.sp_aggregate_trades_to_ohlcv();
DROP FUNCTION IF EXISTS public.sp_aggregate_trades_to_ohlcv(bigint, bigint);
DROP FUNCTION IF EXISTS public.sp_process_features();

/*
 * Пересчитывает свечи для минут, в которых есть необработанные тики.
 *
 * Окно начинается с самого старого тика со статусом 'new' — поэтому данные,
 * приехавшие «позади» уже обработанного участка (докачка дыр, импорт архивов
 * вразнобой), подхватываются автоматически: они просто становятся новым минимумом.
 *
 * Возвращает количество пересчитанных свечей.
 */
CREATE OR REPLACE FUNCTION public.sp_aggregate_new_trades(
    p_window_ms bigint DEFAULT 21600000   -- 6 часов
) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_from    BIGINT;
    v_to      BIGINT;
    v_symbols VARCHAR[];
    v_candles INT := 0;
    m         BIGINT;
BEGIN
    -- Самый старый необработанный тик, округлённый вниз до минуты: свечу надо
    -- пересчитать целиком, а тик может стоять в середине минуты.
    -- Частичный индекс ix_trades_new_tradetime делает это index-only.
    SELECT (MIN("TradeTime") / 60000) * 60000 INTO v_from
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new';

    IF v_from IS NULL THEN
        RETURN 0;
    END IF;

    v_to := v_from + p_window_ms;

    -- Символы, у которых в окне есть необработанные тики.
    --
    -- Список нужен, чтобы запрос лёг на индекс (Symbol, TradeTime): без ограничения
    -- по символу диапазон по одному TradeTime вырождается в seq scan всей партиции
    -- (десятки ГБ). Символы берём из самих данных, а НЕ из TrackedSymbols: тик по
    -- неотслеживаемой паре иначе остался бы 'new' навсегда и заблокировал бы окно.
    SELECT array_agg(DISTINCT "Symbol") INTO v_symbols
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new'
      AND "TradeTime" >= v_from
      AND "TradeTime" <  v_to;

    IF v_symbols IS NULL THEN
        RETURN 0;
    END IF;

    -- Партиции под месяцы окна.
    FOR m IN
        SELECT DISTINCT (EXTRACT(EPOCH FROM d)::BIGINT) * 1000
        FROM generate_series(
            DATE_TRUNC('month', TO_TIMESTAMP(v_from / 1000.0) AT TIME ZONE 'UTC'),
            DATE_TRUNC('month', TO_TIMESTAMP(v_to   / 1000.0) AT TIME ZONE 'UTC'),
            INTERVAL '1 month') AS d
    LOOP
        PERFORM public.sp_ensure_month_partitions(m);
    END LOOP;

    -- Пересчёт свечей окна ЦЕЛИКОМ из всех тиков минуты — включая уже обработанные.
    -- Только так результат не зависит от порядка прихода данных: докачанная дыра
    -- или архив, приехавший «позади», просто попадают в пересчёт.
    --
    -- MIN/MAX по массиву вместо array_agg(ORDER BY): массивы сравниваются
    -- поэлементно, поэтому цена первой и последней сделки берутся потоковым
    -- агрегатом, без сортировки всего окна (она уходила в temp-файлы на диск).
    INSERT INTO public."Ohlcv_1min"
        ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        "Symbol",
        ("TradeTime" / 60000) * 60000                                                  AS "OpenTime",
        (MIN(ARRAY["TradeTime"::numeric, "TradeId"::numeric, "Price"]))[3]::numeric(18,8) AS "OpenPrice",
        MAX("Price")                                                                   AS "HighPrice",
        MIN("Price")                                                                   AS "LowPrice",
        (MAX(ARRAY["TradeTime"::numeric, "TradeId"::numeric, "Price"]))[3]::numeric(18,8) AS "ClosePrice",
        SUM("Quantity")                                                                AS "Volume",
        'new'
    FROM public."Trades"
    WHERE "Symbol" = ANY(v_symbols)
      AND "TradeTime" >= v_from
      AND "TradeTime" <  v_to
      -- Только минуты, где действительно есть необработанные тики. Иначе окно в 6 часов
      -- пересчитывало бы и уже готовые свечи, помечая их 'new' — и feature-pipeline
      -- заново считал бы по ним индикаторы впустую.
      AND (("TradeTime" / 60000) * 60000) IN (
          SELECT DISTINCT ("TradeTime" / 60000) * 60000
          FROM public."Trades"
          WHERE "ProcessingStatus" = 'new'
            AND "TradeTime" >= v_from
            AND "TradeTime" <  v_to
      )
    GROUP BY 1, 2
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "OpenPrice"  = EXCLUDED."OpenPrice",
        "HighPrice"  = EXCLUDED."HighPrice",
        "LowPrice"   = EXCLUDED."LowPrice",
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume"     = EXCLUDED."Volume",
        -- Свеча пересчитана → индикаторы устарели, feature-pipeline возьмёт её заново.
        "ProcessingStatus" = 'new';

    GET DIAGNOSTICS v_candles = ROW_COUNT;

    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE "Symbol" = ANY(v_symbols)
      AND "TradeTime" >= v_from
      AND "TradeTime" <  v_to
      AND "ProcessingStatus" = 'new';

    -- Watermark — только индикатор прогресса. Корректность на нём больше не держится.
    INSERT INTO public."Processing_Watermarks"
        ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES ('OhlcvAggregator', v_to, 'Pending', NOW())
    ON CONFLICT ("ProcessName") DO UPDATE SET
        "LastProcessedTimestamp" = GREATEST(
            public."Processing_Watermarks"."LastProcessedTimestamp", EXCLUDED."LastProcessedTimestamp"),
        "Status"         = 'Pending',
        "LastUpdate_UTC" = NOW();

    RETURN v_candles;
END;
$$;

COMMIT;
