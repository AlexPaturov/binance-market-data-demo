-- Агрегация: LATERAL вместо свободного JOIN — иначе планировщик сканирует всю таблицу.
--
-- В первой версии sp_aggregate_dirty_minutes (миграция 006) пачка минут соединялась
-- с "Trades" обычным JOIN по (Symbol, диапазон TradeTime). На проде планировщик выбирал
-- для этого Merge Join с параллельным seq scan ВСЕХ партиций "Trades" — сотни гигабайт
-- на каждый проход. Оценить, что 10 тысяч точечных заходов по индексу дешевле сплошного
-- скана, он не умеет: селективность диапазонного условия из соседней таблицы ему не видна.
-- Итог тот же, что и до 006: проход не укладывается в командный таймаут и откатывается.
--
-- LATERAL-подзапрос убирает у планировщика сам выбор: на каждую минуту пачки исполняется
-- отдельный параметризованный Index Scan по (Symbol, TradeTime) с runtime-отсечением
-- партиций (в плане все нецелевые партиции — "never executed"). Замер на проде:
-- 200 минут = 6 тысяч блоков (~47 МБ) вместо терабайтного скана.
--
-- Разбор идёт от СВЕЖИХ минут к старым: после простоя или импорта архивов живой график
-- восстанавливается первой же пачкой, а исторический хвост докатывается следом. Для
-- корректности порядок безразличен — пересчёт минуты идемпотентен.
--
--   psql -U bindatacoll -d market_analytics -f 007_aggregate_lateral_scan.sql

BEGIN;

CREATE OR REPLACE FUNCTION public.sp_aggregate_dirty_minutes(
    p_max_minutes integer DEFAULT 10000
) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_candles INT := 0;
    v_max_time BIGINT;
    m BIGINT;
BEGIN
    CREATE TEMP TABLE batch ON COMMIT DROP AS
    SELECT "Symbol", "OpenTime"
    FROM public."DirtyMinutes"
    ORDER BY "OpenTime" DESC
    LIMIT p_max_minutes
    FOR UPDATE SKIP LOCKED;

    IF NOT EXISTS (SELECT 1 FROM batch) THEN
        RETURN 0;
    END IF;

    -- Партиции под месяцы пачки.
    FOR m IN
        SELECT DISTINCT (EXTRACT(EPOCH FROM DATE_TRUNC('month',
            TO_TIMESTAMP("OpenTime" / 1000.0) AT TIME ZONE 'UTC'))::BIGINT) * 1000
        FROM batch
    LOOP
        PERFORM public.sp_ensure_month_partitions(m);
    END LOOP;

    -- Свеча пересчитывается целиком из всех тиков своей минуты — включая те, что уже
    -- участвовали в прошлом расчёте: результат не зависит от порядка прихода данных.
    --
    -- LATERAL обязателен, см. шапку файла. MIN/MAX по массиву вместо
    -- array_agg(ORDER BY): цена первой и последней сделки берутся потоковым агрегатом,
    -- без сортировки тиков минуты.
    INSERT INTO public."Ohlcv_1min"
        ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        b."Symbol",
        b."OpenTime",
        c."OpenPrice", c."HighPrice", c."LowPrice", c."ClosePrice", c."Volume",
        'new'
    FROM batch b
    CROSS JOIN LATERAL (
        SELECT
            (MIN(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "OpenPrice",
            MAX(t."Price")                                                                          AS "HighPrice",
            MIN(t."Price")                                                                          AS "LowPrice",
            (MAX(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "ClosePrice",
            SUM(t."Quantity")                                                                       AS "Volume"
        FROM public."Trades" t
        WHERE t."Symbol" = b."Symbol"
          AND t."TradeTime" >= b."OpenTime"
          AND t."TradeTime" <  b."OpenTime" + 60000
    ) c
    -- Минута без тиков (например, тики удалила ротация партиций): свечи не будет,
    -- но из очереди минута всё равно уйдёт.
    WHERE c."Volume" IS NOT NULL
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "OpenPrice"  = EXCLUDED."OpenPrice",
        "HighPrice"  = EXCLUDED."HighPrice",
        "LowPrice"   = EXCLUDED."LowPrice",
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume"     = EXCLUDED."Volume",
        -- Свеча пересчитана → индикаторы по ней устарели, feature-pipeline возьмёт её заново.
        "ProcessingStatus" = 'new';

    GET DIAGNOSTICS v_candles = ROW_COUNT;

    DELETE FROM public."DirtyMinutes" d
    USING batch b
    WHERE d."Symbol" = b."Symbol"
      AND d."OpenTime" = b."OpenTime";

    SELECT MAX("OpenTime") INTO v_max_time FROM batch;

    -- Watermark — индикатор прогресса. Корректность на нём не держится: работу находит очередь.
    INSERT INTO public."Processing_Watermarks"
        ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES ('OhlcvAggregator', v_max_time + 60000, 'Pending', NOW())
    ON CONFLICT ("ProcessName") DO UPDATE SET
        "LastProcessedTimestamp" = GREATEST(
            public."Processing_Watermarks"."LastProcessedTimestamp", EXCLUDED."LastProcessedTimestamp"),
        "Status"         = 'Pending',
        "LastUpdate_UTC" = NOW();

    RETURN v_candles;
END;
$$;

COMMIT;
