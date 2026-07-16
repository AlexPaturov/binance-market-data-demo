-- CVD-дельта минуты считается агрегацией — в том же проходе по тикам, что и свеча.
--
-- CVD считался отдельным запросом по тикам (`GetCvdForOhlcvAsync`): на каждый расчёт фич
-- тики окна перечитывались с диска заново — случайные чтения по холодным партициям из
-- того же бюджета IOPS, который делят импорт и агрегация. На проде под импортом архивов
-- запрос не укладывался даже в 600 с — при том, что `sp_aggregate_dirty_minutes` эти же
-- тики уже читала для OHLCV. Диск платил дважды за одни данные.
--
-- Теперь дельта минуты (объём покупок минус объём продаж) — ещё один агрегат в том же
-- LATERAL-подзапросе: тик читается один раз и даёт сразу и свечу, и дельту. CVD по окну —
-- оконная сумма дельт ПО СВЕЧАМ, тики для него не читаются вообще.
--
-- NULL в "CvdDelta" — «дельта не посчитана»: свеча построена до этой миграции либо
-- записана путём без тиковых данных (klines API). Читающая сторона в этом случае
-- откатывается на прежний запрос по тикам; повторная агрегация минуты заполняет дельту.
--
-- Знак: IsBuyerMaker = false — агрессор покупатель, объём идёт в плюс (как в прежнем
-- расчёте CVD).
--
-- Обратно совместимо: старый воркер новую колонку не читает, вставки с явным списком
-- колонок оставляют её NULL. Применяется до деплоя воркера.
--
--   psql -U bindatacoll -d market_analytics -f 012_cvd_delta_in_aggregation.sql

BEGIN;

ALTER TABLE public."Ohlcv_1min"
    ADD COLUMN IF NOT EXISTS "CvdDelta" numeric(28,8);

-- Тело — версия миграции 010 (LATERAL, возврат числа снятых минут, NOTIFY), добавлена
-- только дельта CVD в тот же проход по тикам.
CREATE OR REPLACE FUNCTION public.sp_aggregate_dirty_minutes(
    p_max_minutes integer DEFAULT 10000
) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_minutes INT := 0;
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
    -- LATERAL обязателен, см. шапку 007_aggregate_lateral_scan.sql. MIN/MAX по массиву
    -- вместо array_agg(ORDER BY): цена первой и последней сделки берутся потоковым
    -- агрегатом, без сортировки тиков минуты.
    INSERT INTO public."Ohlcv_1min"
        ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "CvdDelta", "ProcessingStatus")
    SELECT
        b."Symbol",
        b."OpenTime",
        c."OpenPrice", c."HighPrice", c."LowPrice", c."ClosePrice", c."Volume",
        c."CvdDelta",
        'new'
    FROM batch b
    CROSS JOIN LATERAL (
        SELECT
            (MIN(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "OpenPrice",
            MAX(t."Price")                                                                          AS "HighPrice",
            MIN(t."Price")                                                                          AS "LowPrice",
            (MAX(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "ClosePrice",
            SUM(t."Quantity")                                                                       AS "Volume",
            SUM(CASE WHEN t."IsBuyerMaker" THEN -t."Quantity" ELSE t."Quantity" END)                AS "CvdDelta"
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
        "CvdDelta"   = EXCLUDED."CvdDelta",
        -- Свеча пересчитана → индикаторы по ней устарели, feature-pipeline возьмёт её заново.
        "ProcessingStatus" = 'new';

    GET DIAGNOSTICS v_candles = ROW_COUNT;

    DELETE FROM public."DirtyMinutes" d
    USING batch b
    WHERE d."Symbol" = b."Symbol"
      AND d."OpenTime" = b."OpenTime";

    GET DIAGNOSTICS v_minutes = ROW_COUNT;

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

    -- Свечи пересчитаны → индикаторы по ним устарели: будим расчёт фич.
    IF v_candles > 0 THEN
        PERFORM pg_notify('candles_new', '');
    END IF;

    RETURN v_minutes;
END;
$$;

COMMIT;
