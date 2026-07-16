--
-- PostgreSQL database dump
--


-- Dumped from database version 16.14
-- Dumped by pg_dump version 16.14

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: fn_partitioned_size_bytes(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.fn_partitioned_size_bytes() RETURNS bigint
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(SUM(pg_total_relation_size(i.inhrelid)), 0)
    FROM pg_inherits i
    WHERE i.inhparent IN (
        'public."Trades"'::regclass,
        'public."Ohlcv_1min"'::regclass,
        'public."Ohlcv_Features"'::regclass,
        'public."OrderBook_Features"'::regclass,
        'public."DataQualityReports"'::regclass,
        'public."DataQualityFindings"'::regclass
    );
$$;


--
-- Name: fn_retention_floor_ms(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.fn_retention_floor_ms() RETURNS bigint
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(
        MIN(
            EXTRACT(EPOCH FROM TO_DATE(
                substring(c.relname FROM 'Trades_(\d{4}_\d{2})'), 'YYYY_MM'
            ))::bigint * 1000
        ),
        0
    )
    FROM pg_inherits i
    JOIN pg_class c ON c.oid = i.inhrelid
    WHERE i.inhparent = 'public."Trades"'::regclass;
$$;


--
-- Name: sp_aggregate_dirty_minutes(integer); Type: FUNCTION; Schema: public; Owner: -
--

-- ВОЗВРАТ: сколько минут снято с очереди, а не сколько построено свечей. Потребитель пьёт
-- очередь до дна и по возврату решает, осталась ли работа. Минута без тиков (их удалила
-- ротация партиций) свечи не даёт, но с очереди уходит — по числу свечей такой кусок
-- выглядел бы как пустая очередь, и разбор останавливался бы на нём, не дойдя до дна.
CREATE FUNCTION public.sp_aggregate_dirty_minutes(p_max_minutes integer DEFAULT 10000) RETURNS integer
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
    -- LATERAL обязателен, см. шапку файла. MIN/MAX по массиву вместо
    -- array_agg(ORDER BY): цена первой и последней сделки берутся потоковым агрегатом,
    -- без сортировки тиков минуты.
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
            -- Дельта CVD: IsBuyerMaker = false — агрессор покупатель, объём в плюс.
            -- Тот же проход по тикам даёт и свечу, и дельту — тики для CVD отдельно
            -- не перечитываются (миграция 012).
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

    -- Свечи пересчитаны → индикаторы по ним устарели: будим расчёт фич (канал `candles_new`).
    IF v_candles > 0 THEN
        PERFORM pg_notify('candles_new', '');
    END IF;

    RETURN v_minutes;
END;
$$;


--
-- Name: sp_bulk_insert_trades(bigint[], character varying[], numeric[], numeric[], numeric[], bigint[], boolean[], boolean[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_bulk_insert_trades(p_trade_ids bigint[], p_symbols character varying[], p_prices numeric[], p_quantities numeric[], p_quote_quantities numeric[], p_trade_times bigint[], p_is_buyer_makers boolean[], p_is_best_matches boolean[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_dirty INT := 0;
BEGIN
    -- Грязными помечаем минуты только тех тиков, что действительно вставились (RETURNING).
    -- Повторный импорт того же архива упирается в ON CONFLICT DO NOTHING и очередь не пачкает:
    -- пересчитывать свечу, в которой ничего не изменилось, незачем.
    WITH inserted AS (
        INSERT INTO public."Trades" (
            "TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity",
            "TradeTime", "IsBuyerMaker", "IsBestMatch"
        )
        SELECT * FROM UNNEST(
            p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities,
            p_trade_times, p_is_buyer_makers, p_is_best_matches
        )
        ON CONFLICT ("TradeId", "Symbol", "TradeTime") DO NOTHING
        RETURNING "Symbol", "TradeTime"
    ),
    dirty AS (
        INSERT INTO public."DirtyMinutes" ("Symbol", "OpenTime")
        SELECT DISTINCT "Symbol", ("TradeTime" / 60000) * 60000
        FROM inserted
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    SELECT count(*) INTO v_dirty FROM dirty;

    -- Появились новые грязные минуты — будим агрегатор (канал `dirty_minutes`).
    -- Уведомление доставляется при COMMIT, поэтому раньше самих данных не придёт.
    IF v_dirty > 0 THEN
        PERFORM pg_notify('dirty_minutes', '');
    END IF;
END;
$$;


--
-- Name: sp_ensure_month_partitions(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_ensure_month_partitions(target_time bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    month_start TIMESTAMP;
    month_end   TIMESTAMP;
    suffix      TEXT;
    from_ms     BIGINT;
    to_ms       BIGINT;
    floor_ms    BIGINT;
BEGIN
    month_start := DATE_TRUNC('month', TO_TIMESTAMP(target_time / 1000.0) AT TIME ZONE 'UTC');
    month_end   := month_start + INTERVAL '1 month';
    suffix      := TO_CHAR(month_start, 'YYYY_MM');
    from_ms     := EXTRACT(EPOCH FROM month_start)::BIGINT * 1000;
    to_ms       := EXTRACT(EPOCH FROM month_end)::BIGINT * 1000;

    -- Барьер против повторной закачки удалённого: партицию ниже границы ретенции
    -- создавать нельзя, иначе аудитор/импорт архивов вернут дропнутый месяц обратно.
    floor_ms := public.fn_retention_floor_ms();
    IF floor_ms > 0 AND from_ms < floor_ms THEN
        RAISE NOTICE 'Месяц % ниже границы ретенции (%) — партиции не создаются.',
            suffix, TO_CHAR(TO_TIMESTAMP(floor_ms / 1000.0) AT TIME ZONE 'UTC', 'YYYY-MM');
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Trades_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
                       'Trades_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Ohlcv_1min_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Ohlcv_1min" FOR VALUES FROM (%s) TO (%s)',
                       'Ohlcv_1min_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Ohlcv_Features_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Ohlcv_Features" FOR VALUES FROM (%s) TO (%s)',
                       'Ohlcv_Features_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'OrderBook_Features_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."OrderBook_Features" FOR VALUES FROM (%s) TO (%s)',
                       'OrderBook_Features_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'DataQualityReports_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."DataQualityReports" FOR VALUES FROM (%L) TO (%L)',
                       'DataQualityReports_' || suffix, month_start::date, month_end::date);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'DataQualityFindings_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."DataQualityFindings" FOR VALUES FROM (%L) TO (%L)',
                       'DataQualityFindings_' || suffix,
                       month_start AT TIME ZONE 'UTC', month_end AT TIME ZONE 'UTC');
    END IF;
END;
$$;


--
-- Name: sp_ensure_trades_partition(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_ensure_trades_partition(target_time bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    PERFORM public.sp_ensure_month_partitions(target_time);
END;
$$;


--
-- Name: sp_find_gaps_in_window(text, bigint, bigint, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_find_gaps_in_window(p_symbol text, p_start_time_ms bigint, p_end_time_ms bigint, p_min_gap_seconds integer) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH WindowTrades AS (
        -- Выбираем сделки только в нашем "окне" + одну сделку ДО него,
        -- чтобы правильно определить первую дыру в блоке.
        SELECT * FROM (
            SELECT "TradeId", "TradeTime"
            FROM public."Trades"
            WHERE "Symbol" = p_symbol AND "TradeTime" < p_start_time_ms
            ORDER BY "TradeTime" DESC
            LIMIT 1
        ) AS before_window
        UNION ALL
        SELECT "TradeId", "TradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
          AND "TradeTime" >= p_start_time_ms
          AND "TradeTime" <= p_end_time_ms
    ),
    OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM WindowTrades
    )
    SELECT
        "PrevTradeTime" AS "GapStart",
        "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE
        "PrevTradeTime" IS NOT NULL AND -- Игнорируем самую первую запись
        ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000);
END;
$$;


--
-- Name: sp_find_trade_gaps(text, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_find_trade_gaps(p_symbol text, p_min_gap_seconds integer) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
    )
    SELECT
        "PrevTradeTime" AS "GapStart",
        "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000)

    UNION ALL

    SELECT
        MAX(t."TradeTime") AS "GapStart",
        (EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT AS "GapEnd"
    FROM public."Trades" t
    WHERE t."Symbol" = p_symbol
    HAVING ((EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT - MAX(t."TradeTime")) > (p_min_gap_seconds * 1000);
END;
$$;


--
-- Name: sp_find_trade_id_gaps_in_window(text, bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_find_trade_id_gaps_in_window(p_symbol text, p_start_trade_id bigint, p_end_trade_id bigint) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT "TradeId", LAG("TradeId", 1) OVER (ORDER BY "TradeId") AS "PrevTradeId"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol AND "TradeId" >= p_start_trade_id AND "TradeId" <= p_end_trade_id
    )
    SELECT 
        "PrevTradeId" AS "GapStart", 
        "TradeId" AS "GapEnd"
    FROM OrderedTrades 
    WHERE "TradeId" > "PrevTradeId" + 1;
END;
$$;


--
-- Name: sp_rotate_partitions(bigint, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_rotate_partitions(p_max_bytes bigint, p_min_months_to_keep integer DEFAULT 6) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    current_month TIMESTAMP;
    target_month  TIMESTAMP;
    oldest_suffix TEXT;
    oldest_month  TIMESTAMP;
    keep_before   TIMESTAMP;
    total_bytes   BIGINT;
    new_floor_ms  BIGINT;
    dropped       INT := 0;
BEGIN
    current_month := DATE_TRUNC('month', NOW() AT TIME ZONE 'UTC');
    keep_before   := current_month - (p_min_months_to_keep || ' months')::INTERVAL;

    FOR target_month IN
        SELECT m FROM generate_series(current_month, current_month + INTERVAL '1 month', INTERVAL '1 month') AS m
    LOOP
        PERFORM public.sp_ensure_month_partitions(
            EXTRACT(EPOCH FROM target_month)::BIGINT * 1000);
    END LOOP;

    LOOP
        total_bytes := public.fn_partitioned_size_bytes();
        EXIT WHEN total_bytes <= p_max_bytes;

        SELECT substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        INTO oldest_suffix
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'public."Trades"'::regclass
        ORDER BY substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        LIMIT 1;

        EXIT WHEN oldest_suffix IS NULL;

        oldest_month := TO_DATE(oldest_suffix, 'YYYY_MM');

        IF oldest_month >= keep_before THEN
            RAISE WARNING
                'Размер % байт выше порога %, но самый старый месяц (%) свежее окна в % мес. Ротация остановлена — нужно расширять диск или поднимать порог.',
                total_bytes, p_max_bytes, oldest_suffix, p_min_months_to_keep;
            EXIT;
        END IF;

        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Trades_'              || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Ohlcv_1min_'          || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Ohlcv_Features_'      || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'OrderBook_Features_'  || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'DataQualityReports_'  || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'DataQualityFindings_' || oldest_suffix);

        dropped := dropped + 1;
        RAISE NOTICE 'Ротация: месяц % удалён во всех таблицах (было % байт, порог %).',
            oldest_suffix, total_bytes, p_max_bytes;
    END LOOP;

    IF dropped > 0 THEN
        new_floor_ms := public.fn_retention_floor_ms();

        UPDATE public."HistoricalAudit_Watermarks"
        SET "LastChecked_Timestamp" = new_floor_ms,
            "LastChecked_TradeId"   = 0
        WHERE "LastChecked_Timestamp" < new_floor_ms;

        RAISE NOTICE 'Ротация завершена: удалено месяцев — %. Новая граница ретенции: %.',
            dropped, TO_CHAR(TO_TIMESTAMP(new_floor_ms / 1000.0) AT TIME ZONE 'UTC', 'YYYY-MM');
    END IF;
END;
$$;


--
-- Name: sp_update_tracked_symbols(character varying[], integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_update_tracked_symbols(p_symbols character varying[], p_max_missed_scans integer DEFAULT 3) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE public."TrackedSymbols"
    SET "MissedScans" = "MissedScans" + 1,
        "IsActive"    = ("MissedScans" + 1) < p_max_missed_scans
    WHERE "IsActive" = TRUE
      AND "Symbol" <> ALL(p_symbols);

    INSERT INTO public."TrackedSymbols" ("Symbol", "IsActive", "LastScanned", "MissedScans")
    SELECT symbol, TRUE, NOW(), 0 FROM UNNEST(p_symbols) AS u(symbol)
    ON CONFLICT ("Symbol") DO UPDATE
    SET "IsActive"    = TRUE,
        "LastScanned" = NOW(),
        "MissedScans" = 0;
END;
$$;


--
-- Name: sp_upsert_ohlcv_features(character varying[], bigint[], numeric[], numeric[], numeric[], numeric[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_upsert_ohlcv_features(p_symbols character varying[], p_open_times bigint[], p_rsi_14 numeric[], p_macd_signals numeric[], p_macd_hists numeric[], p_cvds numeric[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    month_ms BIGINT;
BEGIN
    CREATE TEMP TABLE NewFeatures ON COMMIT DROP AS
    SELECT * FROM UNNEST(
        p_symbols, p_open_times, p_rsi_14, p_macd_signals, p_macd_hists, p_cvds
    ) AS t(
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "CVD"
    );

    FOR month_ms IN SELECT DISTINCT "OpenTime" FROM NewFeatures LOOP
        PERFORM public.sp_ensure_month_partitions(month_ms);
    END LOOP;

    INSERT INTO public."Ohlcv_Features" (
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "CVD"
    )
    SELECT * FROM NewFeatures
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET
        "RSI_14" = EXCLUDED."RSI_14",
        "MACD_Signal" = EXCLUDED."MACD_Signal",
        "MACD_Hist" = EXCLUDED."MACD_Hist",
        "CVD" = EXCLUDED."CVD";
END;
$$;


SET default_tablespace = '';

--
-- Name: DataQualityFindings; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
)
PARTITION BY RANGE ("PeriodFrom");


SET default_table_access_method = heap;

--
-- Name: DataQualityFindings_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_01" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_02" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_03" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_04" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_05" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_06" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_07" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_08" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_09" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_10" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_11" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2026_12" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_01" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_02" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_03" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_04" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_05" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_06" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_07" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_08" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_09" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_10" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_11" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings_2027_12" (
    "Id" bigint NOT NULL,
    "CheckGroup" character varying(32) NOT NULL,
    "CheckType" character varying(64) NOT NULL,
    "Symbol" character varying(20),
    "PeriodFrom" timestamp with time zone NOT NULL,
    "PeriodTo" timestamp with time zone NOT NULL,
    "Severity" character varying(10) NOT NULL,
    "Count" bigint DEFAULT 0 NOT NULL,
    "Details" jsonb,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityFindings_Id_seq1; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."DataQualityFindings" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."DataQualityFindings_Id_seq1"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: DataQualityReports; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
)
PARTITION BY RANGE ("PeriodMonth");


--
-- Name: DataQualityReports_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_01" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_02" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_03" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_04" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_05" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_06" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_07" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_08" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_09" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_10" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_11" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2026_12" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_01" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_02" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_03" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_04" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_05" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_06" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_07" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_08" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_09" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_10" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_11" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityReports_2027_12" (
    "Id" integer NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "PeriodMonth" date NOT NULL,
    "TradeCount" bigint DEFAULT 0 NOT NULL,
    "GapCount" integer DEFAULT 0 NOT NULL,
    "InvalidPriceCount" integer DEFAULT 0 NOT NULL,
    "OutlierCount" integer DEFAULT 0 NOT NULL,
    "Status" character varying(10) DEFAULT 'ok'::character varying NOT NULL,
    "CheckedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: DataQualityReports_Id_seq1; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public."DataQualityReports" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."DataQualityReports_Id_seq1"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: DirtyMinutes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DirtyMinutes" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: HistoricalAudit_Watermarks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."HistoricalAudit_Watermarks" (
    "Symbol" character varying(20) NOT NULL,
    "LastChecked_TradeId" bigint NOT NULL,
    "LastChecked_Timestamp" bigint NOT NULL,
    "Status" character varying(20) NOT NULL,
    "RetryCount" integer DEFAULT 0 NOT NULL,
    "LastAttempt_UTC" timestamp with time zone
);


--
-- Name: Ohlcv_1min; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
)
PARTITION BY RANGE ("OpenTime");


--
-- Name: Ohlcv_1min_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2026_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_1min_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_1min_2027_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "OpenPrice" numeric(18,8) NOT NULL,
    "HighPrice" numeric(18,8) NOT NULL,
    "LowPrice" numeric(18,8) NOT NULL,
    "ClosePrice" numeric(18,8) NOT NULL,
    "Volume" numeric(28,8) NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Ohlcv_Features; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
)
PARTITION BY RANGE ("OpenTime");


--
-- Name: Ohlcv_Features_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2026_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: Ohlcv_Features_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Ohlcv_Features_2027_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "RSI_14" numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist" numeric(18,8),
    "CVD" numeric(28,8)
);


--
-- Name: OrderBook_Features; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
)
PARTITION BY RANGE ("OpenTime");


--
-- Name: OrderBook_Features_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2026_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_01" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_02" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_03" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_04" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_05" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_06" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_07" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_08" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_09" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_10" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_11" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: OrderBook_Features_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OrderBook_Features_2027_12" (
    "Symbol" character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,
    "MidPrice" numeric(18,8) NOT NULL,
    "BestBid" numeric(18,8) NOT NULL,
    "BestAsk" numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,
    "SpreadBps" numeric(12,4) NOT NULL,
    "Imbalance" numeric(10,6) NOT NULL,
    "BidDepth01" numeric(28,8) NOT NULL,
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,
    "AskDepth10" numeric(28,8) NOT NULL,
    "MaxBidWall" numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall" numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,
    "UpdateCount" integer DEFAULT 0 NOT NULL,
    "SampleCount" integer DEFAULT 0 NOT NULL,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: Processing_Watermarks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Processing_Watermarks" (
    "ProcessName" character varying(50) NOT NULL,
    "LastProcessedTimestamp" bigint NOT NULL,
    "Status" character varying(20) NOT NULL,
    "LastUpdate_UTC" timestamp with time zone NOT NULL
);


--
-- Name: TrackedSymbols; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."TrackedSymbols" (
    "Symbol" character varying(20) NOT NULL,
    "IsActive" boolean DEFAULT true NOT NULL,
    "DateAdded" timestamp with time zone DEFAULT now() NOT NULL,
    "LastScanned" timestamp with time zone,
    "MissedScans" integer DEFAULT 0 NOT NULL
);


--
-- Name: Trades; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
)
PARTITION BY RANGE ("TradeTime");


--
-- Name: Trades_2026_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_01" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_02" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_03" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_04" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_05" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_06" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_07" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_08" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_09" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_10" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_11" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2026_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2026_12" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_01" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_02" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_03" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_04" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_05" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_06" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_07" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_08" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_09" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_10" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_11" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: Trades_2027_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2027_12" (
    "TradeId" bigint NOT NULL,
    "Symbol" character varying(20) NOT NULL,
    "Price" numeric(18,8) NOT NULL,
    "Quantity" numeric(28,8) NOT NULL,
    "QuoteQuantity" numeric(28,8) NOT NULL,
    "TradeTime" bigint NOT NULL,
    "IsBuyerMaker" boolean NOT NULL,
    "IsBestMatch" boolean NOT NULL,
    "OrderId" bigint,
    "Commission" numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade" boolean DEFAULT false NOT NULL
);


--
-- Name: DataQualityFindings_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_01" FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2026-02-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_02" FOR VALUES FROM ('2026-02-01 00:00:00+00') TO ('2026-03-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_03" FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_04" FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_05" FOR VALUES FROM ('2026-05-01 00:00:00+00') TO ('2026-06-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_06" FOR VALUES FROM ('2026-06-01 00:00:00+00') TO ('2026-07-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_07" FOR VALUES FROM ('2026-07-01 00:00:00+00') TO ('2026-08-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_08" FOR VALUES FROM ('2026-08-01 00:00:00+00') TO ('2026-09-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_09" FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_10" FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_11" FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');


--
-- Name: DataQualityFindings_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2026_12" FOR VALUES FROM ('2026-12-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_01" FOR VALUES FROM ('2027-01-01 00:00:00+00') TO ('2027-02-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_02" FOR VALUES FROM ('2027-02-01 00:00:00+00') TO ('2027-03-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_03" FOR VALUES FROM ('2027-03-01 00:00:00+00') TO ('2027-04-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_04" FOR VALUES FROM ('2027-04-01 00:00:00+00') TO ('2027-05-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_05" FOR VALUES FROM ('2027-05-01 00:00:00+00') TO ('2027-06-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_06" FOR VALUES FROM ('2027-06-01 00:00:00+00') TO ('2027-07-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_07" FOR VALUES FROM ('2027-07-01 00:00:00+00') TO ('2027-08-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_08" FOR VALUES FROM ('2027-08-01 00:00:00+00') TO ('2027-09-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_09" FOR VALUES FROM ('2027-09-01 00:00:00+00') TO ('2027-10-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_10" FOR VALUES FROM ('2027-10-01 00:00:00+00') TO ('2027-11-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_11" FOR VALUES FROM ('2027-11-01 00:00:00+00') TO ('2027-12-01 00:00:00+00');


--
-- Name: DataQualityFindings_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings" ATTACH PARTITION public."DataQualityFindings_2027_12" FOR VALUES FROM ('2027-12-01 00:00:00+00') TO ('2028-01-01 00:00:00+00');


--
-- Name: DataQualityReports_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_01" FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');


--
-- Name: DataQualityReports_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_02" FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');


--
-- Name: DataQualityReports_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_03" FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');


--
-- Name: DataQualityReports_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_04" FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');


--
-- Name: DataQualityReports_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_05" FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');


--
-- Name: DataQualityReports_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_06" FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');


--
-- Name: DataQualityReports_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_07" FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');


--
-- Name: DataQualityReports_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_08" FOR VALUES FROM ('2026-08-01') TO ('2026-09-01');


--
-- Name: DataQualityReports_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_09" FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');


--
-- Name: DataQualityReports_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_10" FOR VALUES FROM ('2026-10-01') TO ('2026-11-01');


--
-- Name: DataQualityReports_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_11" FOR VALUES FROM ('2026-11-01') TO ('2026-12-01');


--
-- Name: DataQualityReports_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2026_12" FOR VALUES FROM ('2026-12-01') TO ('2027-01-01');


--
-- Name: DataQualityReports_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_01" FOR VALUES FROM ('2027-01-01') TO ('2027-02-01');


--
-- Name: DataQualityReports_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_02" FOR VALUES FROM ('2027-02-01') TO ('2027-03-01');


--
-- Name: DataQualityReports_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_03" FOR VALUES FROM ('2027-03-01') TO ('2027-04-01');


--
-- Name: DataQualityReports_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_04" FOR VALUES FROM ('2027-04-01') TO ('2027-05-01');


--
-- Name: DataQualityReports_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_05" FOR VALUES FROM ('2027-05-01') TO ('2027-06-01');


--
-- Name: DataQualityReports_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_06" FOR VALUES FROM ('2027-06-01') TO ('2027-07-01');


--
-- Name: DataQualityReports_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_07" FOR VALUES FROM ('2027-07-01') TO ('2027-08-01');


--
-- Name: DataQualityReports_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_08" FOR VALUES FROM ('2027-08-01') TO ('2027-09-01');


--
-- Name: DataQualityReports_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_09" FOR VALUES FROM ('2027-09-01') TO ('2027-10-01');


--
-- Name: DataQualityReports_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_10" FOR VALUES FROM ('2027-10-01') TO ('2027-11-01');


--
-- Name: DataQualityReports_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_11" FOR VALUES FROM ('2027-11-01') TO ('2027-12-01');


--
-- Name: DataQualityReports_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ATTACH PARTITION public."DataQualityReports_2027_12" FOR VALUES FROM ('2027-12-01') TO ('2028-01-01');


--
-- Name: Ohlcv_1min_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_01" FOR VALUES FROM ('1767225600000') TO ('1769904000000');


--
-- Name: Ohlcv_1min_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_02" FOR VALUES FROM ('1769904000000') TO ('1772323200000');


--
-- Name: Ohlcv_1min_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_03" FOR VALUES FROM ('1772323200000') TO ('1775001600000');


--
-- Name: Ohlcv_1min_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_04" FOR VALUES FROM ('1775001600000') TO ('1777593600000');


--
-- Name: Ohlcv_1min_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_05" FOR VALUES FROM ('1777593600000') TO ('1780272000000');


--
-- Name: Ohlcv_1min_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_06" FOR VALUES FROM ('1780272000000') TO ('1782864000000');


--
-- Name: Ohlcv_1min_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_07" FOR VALUES FROM ('1782864000000') TO ('1785542400000');


--
-- Name: Ohlcv_1min_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_08" FOR VALUES FROM ('1785542400000') TO ('1788220800000');


--
-- Name: Ohlcv_1min_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_09" FOR VALUES FROM ('1788220800000') TO ('1790812800000');


--
-- Name: Ohlcv_1min_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_10" FOR VALUES FROM ('1790812800000') TO ('1793491200000');


--
-- Name: Ohlcv_1min_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_11" FOR VALUES FROM ('1793491200000') TO ('1796083200000');


--
-- Name: Ohlcv_1min_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_12" FOR VALUES FROM ('1796083200000') TO ('1798761600000');


--
-- Name: Ohlcv_1min_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_01" FOR VALUES FROM ('1798761600000') TO ('1801440000000');


--
-- Name: Ohlcv_1min_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_02" FOR VALUES FROM ('1801440000000') TO ('1803859200000');


--
-- Name: Ohlcv_1min_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_03" FOR VALUES FROM ('1803859200000') TO ('1806537600000');


--
-- Name: Ohlcv_1min_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_04" FOR VALUES FROM ('1806537600000') TO ('1809129600000');


--
-- Name: Ohlcv_1min_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_05" FOR VALUES FROM ('1809129600000') TO ('1811808000000');


--
-- Name: Ohlcv_1min_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_06" FOR VALUES FROM ('1811808000000') TO ('1814400000000');


--
-- Name: Ohlcv_1min_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_07" FOR VALUES FROM ('1814400000000') TO ('1817078400000');


--
-- Name: Ohlcv_1min_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_08" FOR VALUES FROM ('1817078400000') TO ('1819756800000');


--
-- Name: Ohlcv_1min_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_09" FOR VALUES FROM ('1819756800000') TO ('1822348800000');


--
-- Name: Ohlcv_1min_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_10" FOR VALUES FROM ('1822348800000') TO ('1825027200000');


--
-- Name: Ohlcv_1min_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_11" FOR VALUES FROM ('1825027200000') TO ('1827619200000');


--
-- Name: Ohlcv_1min_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_12" FOR VALUES FROM ('1827619200000') TO ('1830297600000');


--
-- Name: Ohlcv_Features_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_01" FOR VALUES FROM ('1767225600000') TO ('1769904000000');


--
-- Name: Ohlcv_Features_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_02" FOR VALUES FROM ('1769904000000') TO ('1772323200000');


--
-- Name: Ohlcv_Features_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_03" FOR VALUES FROM ('1772323200000') TO ('1775001600000');


--
-- Name: Ohlcv_Features_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_04" FOR VALUES FROM ('1775001600000') TO ('1777593600000');


--
-- Name: Ohlcv_Features_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_05" FOR VALUES FROM ('1777593600000') TO ('1780272000000');


--
-- Name: Ohlcv_Features_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_06" FOR VALUES FROM ('1780272000000') TO ('1782864000000');


--
-- Name: Ohlcv_Features_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_07" FOR VALUES FROM ('1782864000000') TO ('1785542400000');


--
-- Name: Ohlcv_Features_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_08" FOR VALUES FROM ('1785542400000') TO ('1788220800000');


--
-- Name: Ohlcv_Features_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_09" FOR VALUES FROM ('1788220800000') TO ('1790812800000');


--
-- Name: Ohlcv_Features_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_10" FOR VALUES FROM ('1790812800000') TO ('1793491200000');


--
-- Name: Ohlcv_Features_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_11" FOR VALUES FROM ('1793491200000') TO ('1796083200000');


--
-- Name: Ohlcv_Features_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2026_12" FOR VALUES FROM ('1796083200000') TO ('1798761600000');


--
-- Name: Ohlcv_Features_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_01" FOR VALUES FROM ('1798761600000') TO ('1801440000000');


--
-- Name: Ohlcv_Features_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_02" FOR VALUES FROM ('1801440000000') TO ('1803859200000');


--
-- Name: Ohlcv_Features_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_03" FOR VALUES FROM ('1803859200000') TO ('1806537600000');


--
-- Name: Ohlcv_Features_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_04" FOR VALUES FROM ('1806537600000') TO ('1809129600000');


--
-- Name: Ohlcv_Features_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_05" FOR VALUES FROM ('1809129600000') TO ('1811808000000');


--
-- Name: Ohlcv_Features_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_06" FOR VALUES FROM ('1811808000000') TO ('1814400000000');


--
-- Name: Ohlcv_Features_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_07" FOR VALUES FROM ('1814400000000') TO ('1817078400000');


--
-- Name: Ohlcv_Features_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_08" FOR VALUES FROM ('1817078400000') TO ('1819756800000');


--
-- Name: Ohlcv_Features_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_09" FOR VALUES FROM ('1819756800000') TO ('1822348800000');


--
-- Name: Ohlcv_Features_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_10" FOR VALUES FROM ('1822348800000') TO ('1825027200000');


--
-- Name: Ohlcv_Features_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_11" FOR VALUES FROM ('1825027200000') TO ('1827619200000');


--
-- Name: Ohlcv_Features_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features" ATTACH PARTITION public."Ohlcv_Features_2027_12" FOR VALUES FROM ('1827619200000') TO ('1830297600000');


--
-- Name: OrderBook_Features_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_01" FOR VALUES FROM ('1767225600000') TO ('1769904000000');


--
-- Name: OrderBook_Features_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_02" FOR VALUES FROM ('1769904000000') TO ('1772323200000');


--
-- Name: OrderBook_Features_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_03" FOR VALUES FROM ('1772323200000') TO ('1775001600000');


--
-- Name: OrderBook_Features_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_04" FOR VALUES FROM ('1775001600000') TO ('1777593600000');


--
-- Name: OrderBook_Features_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_05" FOR VALUES FROM ('1777593600000') TO ('1780272000000');


--
-- Name: OrderBook_Features_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_06" FOR VALUES FROM ('1780272000000') TO ('1782864000000');


--
-- Name: OrderBook_Features_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_07" FOR VALUES FROM ('1782864000000') TO ('1785542400000');


--
-- Name: OrderBook_Features_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_08" FOR VALUES FROM ('1785542400000') TO ('1788220800000');


--
-- Name: OrderBook_Features_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_09" FOR VALUES FROM ('1788220800000') TO ('1790812800000');


--
-- Name: OrderBook_Features_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_10" FOR VALUES FROM ('1790812800000') TO ('1793491200000');


--
-- Name: OrderBook_Features_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_11" FOR VALUES FROM ('1793491200000') TO ('1796083200000');


--
-- Name: OrderBook_Features_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_12" FOR VALUES FROM ('1796083200000') TO ('1798761600000');


--
-- Name: OrderBook_Features_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_01" FOR VALUES FROM ('1798761600000') TO ('1801440000000');


--
-- Name: OrderBook_Features_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_02" FOR VALUES FROM ('1801440000000') TO ('1803859200000');


--
-- Name: OrderBook_Features_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_03" FOR VALUES FROM ('1803859200000') TO ('1806537600000');


--
-- Name: OrderBook_Features_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_04" FOR VALUES FROM ('1806537600000') TO ('1809129600000');


--
-- Name: OrderBook_Features_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_05" FOR VALUES FROM ('1809129600000') TO ('1811808000000');


--
-- Name: OrderBook_Features_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_06" FOR VALUES FROM ('1811808000000') TO ('1814400000000');


--
-- Name: OrderBook_Features_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_07" FOR VALUES FROM ('1814400000000') TO ('1817078400000');


--
-- Name: OrderBook_Features_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_08" FOR VALUES FROM ('1817078400000') TO ('1819756800000');


--
-- Name: OrderBook_Features_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_09" FOR VALUES FROM ('1819756800000') TO ('1822348800000');


--
-- Name: OrderBook_Features_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_10" FOR VALUES FROM ('1822348800000') TO ('1825027200000');


--
-- Name: OrderBook_Features_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_11" FOR VALUES FROM ('1825027200000') TO ('1827619200000');


--
-- Name: OrderBook_Features_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_12" FOR VALUES FROM ('1827619200000') TO ('1830297600000');


--
-- Name: Trades_2026_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_01" FOR VALUES FROM ('1767225600000') TO ('1769904000000');


--
-- Name: Trades_2026_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_02" FOR VALUES FROM ('1769904000000') TO ('1772323200000');


--
-- Name: Trades_2026_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_03" FOR VALUES FROM ('1772323200000') TO ('1775001600000');


--
-- Name: Trades_2026_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_04" FOR VALUES FROM ('1775001600000') TO ('1777593600000');


--
-- Name: Trades_2026_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_05" FOR VALUES FROM ('1777593600000') TO ('1780272000000');


--
-- Name: Trades_2026_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_06" FOR VALUES FROM ('1780272000000') TO ('1782864000000');


--
-- Name: Trades_2026_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_07" FOR VALUES FROM ('1782864000000') TO ('1785542400000');


--
-- Name: Trades_2026_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_08" FOR VALUES FROM ('1785542400000') TO ('1788220800000');


--
-- Name: Trades_2026_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_09" FOR VALUES FROM ('1788220800000') TO ('1790812800000');


--
-- Name: Trades_2026_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_10" FOR VALUES FROM ('1790812800000') TO ('1793491200000');


--
-- Name: Trades_2026_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_11" FOR VALUES FROM ('1793491200000') TO ('1796083200000');


--
-- Name: Trades_2026_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2026_12" FOR VALUES FROM ('1796083200000') TO ('1798761600000');


--
-- Name: Trades_2027_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_01" FOR VALUES FROM ('1798761600000') TO ('1801440000000');


--
-- Name: Trades_2027_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_02" FOR VALUES FROM ('1801440000000') TO ('1803859200000');


--
-- Name: Trades_2027_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_03" FOR VALUES FROM ('1803859200000') TO ('1806537600000');


--
-- Name: Trades_2027_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_04" FOR VALUES FROM ('1806537600000') TO ('1809129600000');


--
-- Name: Trades_2027_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_05" FOR VALUES FROM ('1809129600000') TO ('1811808000000');


--
-- Name: Trades_2027_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_06" FOR VALUES FROM ('1811808000000') TO ('1814400000000');


--
-- Name: Trades_2027_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_07" FOR VALUES FROM ('1814400000000') TO ('1817078400000');


--
-- Name: Trades_2027_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_08" FOR VALUES FROM ('1817078400000') TO ('1819756800000');


--
-- Name: Trades_2027_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_09" FOR VALUES FROM ('1819756800000') TO ('1822348800000');


--
-- Name: Trades_2027_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_10" FOR VALUES FROM ('1822348800000') TO ('1825027200000');


--
-- Name: Trades_2027_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_11" FOR VALUES FROM ('1825027200000') TO ('1827619200000');


--
-- Name: Trades_2027_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2027_12" FOR VALUES FROM ('1827619200000') TO ('1830297600000');


--
-- Name: DataQualityFindings DataQualityFindings_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings"
    ADD CONSTRAINT "DataQualityFindings_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_01 DataQualityFindings_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_01"
    ADD CONSTRAINT "DataQualityFindings_2026_01_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_02 DataQualityFindings_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_02"
    ADD CONSTRAINT "DataQualityFindings_2026_02_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_03 DataQualityFindings_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_03"
    ADD CONSTRAINT "DataQualityFindings_2026_03_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_04 DataQualityFindings_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_04"
    ADD CONSTRAINT "DataQualityFindings_2026_04_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_05 DataQualityFindings_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_05"
    ADD CONSTRAINT "DataQualityFindings_2026_05_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_06 DataQualityFindings_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_06"
    ADD CONSTRAINT "DataQualityFindings_2026_06_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_07 DataQualityFindings_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_07"
    ADD CONSTRAINT "DataQualityFindings_2026_07_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_08 DataQualityFindings_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_08"
    ADD CONSTRAINT "DataQualityFindings_2026_08_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_09 DataQualityFindings_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_09"
    ADD CONSTRAINT "DataQualityFindings_2026_09_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_10 DataQualityFindings_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_10"
    ADD CONSTRAINT "DataQualityFindings_2026_10_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_11 DataQualityFindings_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_11"
    ADD CONSTRAINT "DataQualityFindings_2026_11_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2026_12 DataQualityFindings_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2026_12"
    ADD CONSTRAINT "DataQualityFindings_2026_12_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_01 DataQualityFindings_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_01"
    ADD CONSTRAINT "DataQualityFindings_2027_01_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_02 DataQualityFindings_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_02"
    ADD CONSTRAINT "DataQualityFindings_2027_02_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_03 DataQualityFindings_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_03"
    ADD CONSTRAINT "DataQualityFindings_2027_03_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_04 DataQualityFindings_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_04"
    ADD CONSTRAINT "DataQualityFindings_2027_04_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_05 DataQualityFindings_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_05"
    ADD CONSTRAINT "DataQualityFindings_2027_05_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_06 DataQualityFindings_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_06"
    ADD CONSTRAINT "DataQualityFindings_2027_06_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_07 DataQualityFindings_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_07"
    ADD CONSTRAINT "DataQualityFindings_2027_07_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_08 DataQualityFindings_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_08"
    ADD CONSTRAINT "DataQualityFindings_2027_08_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_09 DataQualityFindings_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_09"
    ADD CONSTRAINT "DataQualityFindings_2027_09_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_10 DataQualityFindings_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_10"
    ADD CONSTRAINT "DataQualityFindings_2027_10_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_11 DataQualityFindings_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_11"
    ADD CONSTRAINT "DataQualityFindings_2027_11_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityFindings_2027_12 DataQualityFindings_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings_2027_12"
    ADD CONSTRAINT "DataQualityFindings_2027_12_pkey" PRIMARY KEY ("Id", "PeriodFrom");


--
-- Name: DataQualityReports DataQualityReports_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports"
    ADD CONSTRAINT "DataQualityReports_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_01 DataQualityReports_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_01"
    ADD CONSTRAINT "DataQualityReports_2026_01_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_02 DataQualityReports_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_02"
    ADD CONSTRAINT "DataQualityReports_2026_02_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_03 DataQualityReports_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_03"
    ADD CONSTRAINT "DataQualityReports_2026_03_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_04 DataQualityReports_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_04"
    ADD CONSTRAINT "DataQualityReports_2026_04_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_05 DataQualityReports_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_05"
    ADD CONSTRAINT "DataQualityReports_2026_05_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_06 DataQualityReports_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_06"
    ADD CONSTRAINT "DataQualityReports_2026_06_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_07 DataQualityReports_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_07"
    ADD CONSTRAINT "DataQualityReports_2026_07_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_08 DataQualityReports_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_08"
    ADD CONSTRAINT "DataQualityReports_2026_08_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_09 DataQualityReports_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_09"
    ADD CONSTRAINT "DataQualityReports_2026_09_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_10 DataQualityReports_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_10"
    ADD CONSTRAINT "DataQualityReports_2026_10_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_11 DataQualityReports_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_11"
    ADD CONSTRAINT "DataQualityReports_2026_11_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2026_12 DataQualityReports_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2026_12"
    ADD CONSTRAINT "DataQualityReports_2026_12_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_01 DataQualityReports_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_01"
    ADD CONSTRAINT "DataQualityReports_2027_01_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_02 DataQualityReports_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_02"
    ADD CONSTRAINT "DataQualityReports_2027_02_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_03 DataQualityReports_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_03"
    ADD CONSTRAINT "DataQualityReports_2027_03_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_04 DataQualityReports_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_04"
    ADD CONSTRAINT "DataQualityReports_2027_04_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_05 DataQualityReports_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_05"
    ADD CONSTRAINT "DataQualityReports_2027_05_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_06 DataQualityReports_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_06"
    ADD CONSTRAINT "DataQualityReports_2027_06_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_07 DataQualityReports_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_07"
    ADD CONSTRAINT "DataQualityReports_2027_07_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_08 DataQualityReports_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_08"
    ADD CONSTRAINT "DataQualityReports_2027_08_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_09 DataQualityReports_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_09"
    ADD CONSTRAINT "DataQualityReports_2027_09_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_10 DataQualityReports_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_10"
    ADD CONSTRAINT "DataQualityReports_2027_10_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_11 DataQualityReports_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_11"
    ADD CONSTRAINT "DataQualityReports_2027_11_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: DataQualityReports_2027_12 DataQualityReports_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports_2027_12"
    ADD CONSTRAINT "DataQualityReports_2027_12_pkey" PRIMARY KEY ("Id", "PeriodMonth");


--
-- Name: HistoricalAudit_Watermarks HistoricalAudit_Watermarks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."HistoricalAudit_Watermarks"
    ADD CONSTRAINT "HistoricalAudit_Watermarks_pkey" PRIMARY KEY ("Symbol");


--
-- Name: Ohlcv_1min PK_Ohlcv_1min; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min"
    ADD CONSTRAINT "PK_Ohlcv_1min" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_01 Ohlcv_1min_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_01"
    ADD CONSTRAINT "Ohlcv_1min_2026_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_02 Ohlcv_1min_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_02"
    ADD CONSTRAINT "Ohlcv_1min_2026_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_03 Ohlcv_1min_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_03"
    ADD CONSTRAINT "Ohlcv_1min_2026_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_04 Ohlcv_1min_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_04"
    ADD CONSTRAINT "Ohlcv_1min_2026_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_05 Ohlcv_1min_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_05"
    ADD CONSTRAINT "Ohlcv_1min_2026_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_06 Ohlcv_1min_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_06"
    ADD CONSTRAINT "Ohlcv_1min_2026_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_07 Ohlcv_1min_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_07"
    ADD CONSTRAINT "Ohlcv_1min_2026_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_08 Ohlcv_1min_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_08"
    ADD CONSTRAINT "Ohlcv_1min_2026_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_09 Ohlcv_1min_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_09"
    ADD CONSTRAINT "Ohlcv_1min_2026_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_10 Ohlcv_1min_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_10"
    ADD CONSTRAINT "Ohlcv_1min_2026_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_11 Ohlcv_1min_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_11"
    ADD CONSTRAINT "Ohlcv_1min_2026_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2026_12 Ohlcv_1min_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2026_12"
    ADD CONSTRAINT "Ohlcv_1min_2026_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_01 Ohlcv_1min_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_01"
    ADD CONSTRAINT "Ohlcv_1min_2027_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_02 Ohlcv_1min_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_02"
    ADD CONSTRAINT "Ohlcv_1min_2027_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_03 Ohlcv_1min_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_03"
    ADD CONSTRAINT "Ohlcv_1min_2027_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_04 Ohlcv_1min_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_04"
    ADD CONSTRAINT "Ohlcv_1min_2027_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_05 Ohlcv_1min_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_05"
    ADD CONSTRAINT "Ohlcv_1min_2027_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_06 Ohlcv_1min_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_06"
    ADD CONSTRAINT "Ohlcv_1min_2027_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_07 Ohlcv_1min_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_07"
    ADD CONSTRAINT "Ohlcv_1min_2027_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_08 Ohlcv_1min_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_08"
    ADD CONSTRAINT "Ohlcv_1min_2027_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_09 Ohlcv_1min_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_09"
    ADD CONSTRAINT "Ohlcv_1min_2027_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_10 Ohlcv_1min_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_10"
    ADD CONSTRAINT "Ohlcv_1min_2027_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_11 Ohlcv_1min_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_11"
    ADD CONSTRAINT "Ohlcv_1min_2027_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min_2027_12 Ohlcv_1min_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min_2027_12"
    ADD CONSTRAINT "Ohlcv_1min_2027_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features Ohlcv_Features_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features"
    ADD CONSTRAINT "Ohlcv_Features_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_01 Ohlcv_Features_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_01"
    ADD CONSTRAINT "Ohlcv_Features_2026_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_02 Ohlcv_Features_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_02"
    ADD CONSTRAINT "Ohlcv_Features_2026_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_03 Ohlcv_Features_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_03"
    ADD CONSTRAINT "Ohlcv_Features_2026_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_04 Ohlcv_Features_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_04"
    ADD CONSTRAINT "Ohlcv_Features_2026_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_05 Ohlcv_Features_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_05"
    ADD CONSTRAINT "Ohlcv_Features_2026_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_06 Ohlcv_Features_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_06"
    ADD CONSTRAINT "Ohlcv_Features_2026_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_07 Ohlcv_Features_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_07"
    ADD CONSTRAINT "Ohlcv_Features_2026_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_08 Ohlcv_Features_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_08"
    ADD CONSTRAINT "Ohlcv_Features_2026_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_09 Ohlcv_Features_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_09"
    ADD CONSTRAINT "Ohlcv_Features_2026_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_10 Ohlcv_Features_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_10"
    ADD CONSTRAINT "Ohlcv_Features_2026_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_11 Ohlcv_Features_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_11"
    ADD CONSTRAINT "Ohlcv_Features_2026_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2026_12 Ohlcv_Features_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2026_12"
    ADD CONSTRAINT "Ohlcv_Features_2026_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_01 Ohlcv_Features_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_01"
    ADD CONSTRAINT "Ohlcv_Features_2027_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_02 Ohlcv_Features_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_02"
    ADD CONSTRAINT "Ohlcv_Features_2027_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_03 Ohlcv_Features_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_03"
    ADD CONSTRAINT "Ohlcv_Features_2027_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_04 Ohlcv_Features_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_04"
    ADD CONSTRAINT "Ohlcv_Features_2027_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_05 Ohlcv_Features_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_05"
    ADD CONSTRAINT "Ohlcv_Features_2027_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_06 Ohlcv_Features_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_06"
    ADD CONSTRAINT "Ohlcv_Features_2027_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_07 Ohlcv_Features_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_07"
    ADD CONSTRAINT "Ohlcv_Features_2027_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_08 Ohlcv_Features_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_08"
    ADD CONSTRAINT "Ohlcv_Features_2027_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_09 Ohlcv_Features_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_09"
    ADD CONSTRAINT "Ohlcv_Features_2027_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_10 Ohlcv_Features_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_10"
    ADD CONSTRAINT "Ohlcv_Features_2027_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_11 Ohlcv_Features_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_11"
    ADD CONSTRAINT "Ohlcv_Features_2027_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_Features_2027_12 Ohlcv_Features_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features_2027_12"
    ADD CONSTRAINT "Ohlcv_Features_2027_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features PK_OrderBook_Features; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features"
    ADD CONSTRAINT "PK_OrderBook_Features" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_01 OrderBook_Features_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_01"
    ADD CONSTRAINT "OrderBook_Features_2026_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_02 OrderBook_Features_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_02"
    ADD CONSTRAINT "OrderBook_Features_2026_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_03 OrderBook_Features_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_03"
    ADD CONSTRAINT "OrderBook_Features_2026_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_04 OrderBook_Features_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_04"
    ADD CONSTRAINT "OrderBook_Features_2026_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_05 OrderBook_Features_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_05"
    ADD CONSTRAINT "OrderBook_Features_2026_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_06 OrderBook_Features_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_06"
    ADD CONSTRAINT "OrderBook_Features_2026_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_07 OrderBook_Features_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_07"
    ADD CONSTRAINT "OrderBook_Features_2026_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_08 OrderBook_Features_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_08"
    ADD CONSTRAINT "OrderBook_Features_2026_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_09 OrderBook_Features_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_09"
    ADD CONSTRAINT "OrderBook_Features_2026_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_10 OrderBook_Features_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_10"
    ADD CONSTRAINT "OrderBook_Features_2026_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_11 OrderBook_Features_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_11"
    ADD CONSTRAINT "OrderBook_Features_2026_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2026_12 OrderBook_Features_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2026_12"
    ADD CONSTRAINT "OrderBook_Features_2026_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_01 OrderBook_Features_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_01"
    ADD CONSTRAINT "OrderBook_Features_2027_01_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_02 OrderBook_Features_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_02"
    ADD CONSTRAINT "OrderBook_Features_2027_02_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_03 OrderBook_Features_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_03"
    ADD CONSTRAINT "OrderBook_Features_2027_03_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_04 OrderBook_Features_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_04"
    ADD CONSTRAINT "OrderBook_Features_2027_04_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_05 OrderBook_Features_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_05"
    ADD CONSTRAINT "OrderBook_Features_2027_05_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_06 OrderBook_Features_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_06"
    ADD CONSTRAINT "OrderBook_Features_2027_06_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_07 OrderBook_Features_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_07"
    ADD CONSTRAINT "OrderBook_Features_2027_07_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_08 OrderBook_Features_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_08"
    ADD CONSTRAINT "OrderBook_Features_2027_08_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_09 OrderBook_Features_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_09"
    ADD CONSTRAINT "OrderBook_Features_2027_09_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_10 OrderBook_Features_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_10"
    ADD CONSTRAINT "OrderBook_Features_2027_10_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_11 OrderBook_Features_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_11"
    ADD CONSTRAINT "OrderBook_Features_2027_11_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: OrderBook_Features_2027_12 OrderBook_Features_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OrderBook_Features_2027_12"
    ADD CONSTRAINT "OrderBook_Features_2027_12_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: DirtyMinutes PK_DirtyMinutes; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DirtyMinutes"
    ADD CONSTRAINT "PK_DirtyMinutes" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Processing_Watermarks Processing_Watermarks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Processing_Watermarks"
    ADD CONSTRAINT "Processing_Watermarks_pkey" PRIMARY KEY ("ProcessName");


--
-- Name: TrackedSymbols TrackedSymbols_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TrackedSymbols"
    ADD CONSTRAINT "TrackedSymbols_pkey" PRIMARY KEY ("Symbol");


--
-- Name: Trades Trades_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades"
    ADD CONSTRAINT "Trades_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_01 Trades_2026_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_01"
    ADD CONSTRAINT "Trades_2026_01_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_02 Trades_2026_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_02"
    ADD CONSTRAINT "Trades_2026_02_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_03 Trades_2026_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_03"
    ADD CONSTRAINT "Trades_2026_03_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_04 Trades_2026_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_04"
    ADD CONSTRAINT "Trades_2026_04_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_05 Trades_2026_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_05"
    ADD CONSTRAINT "Trades_2026_05_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_06 Trades_2026_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_06"
    ADD CONSTRAINT "Trades_2026_06_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_07 Trades_2026_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_07"
    ADD CONSTRAINT "Trades_2026_07_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_08 Trades_2026_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_08"
    ADD CONSTRAINT "Trades_2026_08_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_09 Trades_2026_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_09"
    ADD CONSTRAINT "Trades_2026_09_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_10 Trades_2026_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_10"
    ADD CONSTRAINT "Trades_2026_10_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_11 Trades_2026_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_11"
    ADD CONSTRAINT "Trades_2026_11_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2026_12 Trades_2026_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2026_12"
    ADD CONSTRAINT "Trades_2026_12_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_01 Trades_2027_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_01"
    ADD CONSTRAINT "Trades_2027_01_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_02 Trades_2027_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_02"
    ADD CONSTRAINT "Trades_2027_02_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_03 Trades_2027_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_03"
    ADD CONSTRAINT "Trades_2027_03_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_04 Trades_2027_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_04"
    ADD CONSTRAINT "Trades_2027_04_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_05 Trades_2027_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_05"
    ADD CONSTRAINT "Trades_2027_05_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_06 Trades_2027_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_06"
    ADD CONSTRAINT "Trades_2027_06_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_07 Trades_2027_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_07"
    ADD CONSTRAINT "Trades_2027_07_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_08 Trades_2027_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_08"
    ADD CONSTRAINT "Trades_2027_08_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_09 Trades_2027_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_09"
    ADD CONSTRAINT "Trades_2027_09_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_10 Trades_2027_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_10"
    ADD CONSTRAINT "Trades_2027_10_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_11 Trades_2027_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_11"
    ADD CONSTRAINT "Trades_2027_11_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2027_12 Trades_2027_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2027_12"
    ADD CONSTRAINT "Trades_2027_12_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: ix_dqf_group_symbol; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_group_symbol ON ONLY public."DataQualityFindings" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_01_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_01_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_01" USING btree ("CheckGroup", "Symbol");


--
-- Name: ix_dqf_checked_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_checked_at ON ONLY public."DataQualityFindings" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_01_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_01_CheckedAt_idx" ON public."DataQualityFindings_2026_01" USING btree ("CheckedAt" DESC);


--
-- Name: ix_dqf_severity; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_severity ON ONLY public."DataQualityFindings" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_01_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_01_Severity_idx" ON public."DataQualityFindings_2026_01" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_02_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_02_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_02" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_02_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_02_CheckedAt_idx" ON public."DataQualityFindings_2026_02" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_02_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_02_Severity_idx" ON public."DataQualityFindings_2026_02" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_03_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_03_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_03" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_03_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_03_CheckedAt_idx" ON public."DataQualityFindings_2026_03" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_03_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_03_Severity_idx" ON public."DataQualityFindings_2026_03" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_04_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_04_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_04" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_04_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_04_CheckedAt_idx" ON public."DataQualityFindings_2026_04" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_04_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_04_Severity_idx" ON public."DataQualityFindings_2026_04" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_05_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_05_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_05" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_05_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_05_CheckedAt_idx" ON public."DataQualityFindings_2026_05" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_05_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_05_Severity_idx" ON public."DataQualityFindings_2026_05" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_06_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_06_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_06" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_06_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_06_CheckedAt_idx" ON public."DataQualityFindings_2026_06" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_06_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_06_Severity_idx" ON public."DataQualityFindings_2026_06" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_07_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_07_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_07" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_07_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_07_CheckedAt_idx" ON public."DataQualityFindings_2026_07" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_07_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_07_Severity_idx" ON public."DataQualityFindings_2026_07" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_08_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_08_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_08" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_08_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_08_CheckedAt_idx" ON public."DataQualityFindings_2026_08" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_08_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_08_Severity_idx" ON public."DataQualityFindings_2026_08" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_09_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_09_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_09" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_09_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_09_CheckedAt_idx" ON public."DataQualityFindings_2026_09" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_09_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_09_Severity_idx" ON public."DataQualityFindings_2026_09" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_10_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_10_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_10" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_10_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_10_CheckedAt_idx" ON public."DataQualityFindings_2026_10" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_10_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_10_Severity_idx" ON public."DataQualityFindings_2026_10" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_11_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_11_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_11" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_11_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_11_CheckedAt_idx" ON public."DataQualityFindings_2026_11" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_11_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_11_Severity_idx" ON public."DataQualityFindings_2026_11" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2026_12_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_12_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2026_12" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2026_12_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_12_CheckedAt_idx" ON public."DataQualityFindings_2026_12" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2026_12_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2026_12_Severity_idx" ON public."DataQualityFindings_2026_12" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_01_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_01_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_01" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_01_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_01_CheckedAt_idx" ON public."DataQualityFindings_2027_01" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_01_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_01_Severity_idx" ON public."DataQualityFindings_2027_01" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_02_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_02_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_02" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_02_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_02_CheckedAt_idx" ON public."DataQualityFindings_2027_02" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_02_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_02_Severity_idx" ON public."DataQualityFindings_2027_02" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_03_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_03_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_03" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_03_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_03_CheckedAt_idx" ON public."DataQualityFindings_2027_03" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_03_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_03_Severity_idx" ON public."DataQualityFindings_2027_03" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_04_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_04_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_04" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_04_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_04_CheckedAt_idx" ON public."DataQualityFindings_2027_04" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_04_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_04_Severity_idx" ON public."DataQualityFindings_2027_04" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_05_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_05_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_05" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_05_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_05_CheckedAt_idx" ON public."DataQualityFindings_2027_05" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_05_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_05_Severity_idx" ON public."DataQualityFindings_2027_05" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_06_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_06_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_06" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_06_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_06_CheckedAt_idx" ON public."DataQualityFindings_2027_06" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_06_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_06_Severity_idx" ON public."DataQualityFindings_2027_06" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_07_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_07_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_07" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_07_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_07_CheckedAt_idx" ON public."DataQualityFindings_2027_07" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_07_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_07_Severity_idx" ON public."DataQualityFindings_2027_07" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_08_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_08_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_08" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_08_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_08_CheckedAt_idx" ON public."DataQualityFindings_2027_08" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_08_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_08_Severity_idx" ON public."DataQualityFindings_2027_08" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_09_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_09_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_09" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_09_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_09_CheckedAt_idx" ON public."DataQualityFindings_2027_09" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_09_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_09_Severity_idx" ON public."DataQualityFindings_2027_09" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_10_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_10_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_10" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_10_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_10_CheckedAt_idx" ON public."DataQualityFindings_2027_10" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_10_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_10_Severity_idx" ON public."DataQualityFindings_2027_10" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_11_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_11_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_11" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_11_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_11_CheckedAt_idx" ON public."DataQualityFindings_2027_11" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_11_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_11_Severity_idx" ON public."DataQualityFindings_2027_11" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: DataQualityFindings_2027_12_CheckGroup_Symbol_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_12_CheckGroup_Symbol_idx" ON public."DataQualityFindings_2027_12" USING btree ("CheckGroup", "Symbol");


--
-- Name: DataQualityFindings_2027_12_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_12_CheckedAt_idx" ON public."DataQualityFindings_2027_12" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityFindings_2027_12_Severity_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityFindings_2027_12_Severity_idx" ON public."DataQualityFindings_2027_12" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: ix_dqr_checked_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqr_checked_at ON ONLY public."DataQualityReports" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_01_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_01_CheckedAt_idx" ON public."DataQualityReports_2026_01" USING btree ("CheckedAt" DESC);


--
-- Name: ix_dqr_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqr_status ON ONLY public."DataQualityReports" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_01_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_01_Status_idx" ON public."DataQualityReports_2026_01" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: ix_dqr_symbol_month; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ix_dqr_symbol_month ON ONLY public."DataQualityReports" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_01_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_01_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_01" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_02_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_02_CheckedAt_idx" ON public."DataQualityReports_2026_02" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_02_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_02_Status_idx" ON public."DataQualityReports_2026_02" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_02_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_02_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_02" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_03_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_03_CheckedAt_idx" ON public."DataQualityReports_2026_03" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_03_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_03_Status_idx" ON public."DataQualityReports_2026_03" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_03_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_03_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_03" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_04_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_04_CheckedAt_idx" ON public."DataQualityReports_2026_04" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_04_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_04_Status_idx" ON public."DataQualityReports_2026_04" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_04_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_04_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_04" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_05_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_05_CheckedAt_idx" ON public."DataQualityReports_2026_05" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_05_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_05_Status_idx" ON public."DataQualityReports_2026_05" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_05_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_05_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_05" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_06_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_06_CheckedAt_idx" ON public."DataQualityReports_2026_06" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_06_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_06_Status_idx" ON public."DataQualityReports_2026_06" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_06_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_06_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_06" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_07_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_07_CheckedAt_idx" ON public."DataQualityReports_2026_07" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_07_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_07_Status_idx" ON public."DataQualityReports_2026_07" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_07_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_07_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_07" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_08_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_08_CheckedAt_idx" ON public."DataQualityReports_2026_08" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_08_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_08_Status_idx" ON public."DataQualityReports_2026_08" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_08_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_08_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_08" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_09_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_09_CheckedAt_idx" ON public."DataQualityReports_2026_09" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_09_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_09_Status_idx" ON public."DataQualityReports_2026_09" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_09_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_09_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_09" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_10_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_10_CheckedAt_idx" ON public."DataQualityReports_2026_10" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_10_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_10_Status_idx" ON public."DataQualityReports_2026_10" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_10_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_10_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_10" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_11_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_11_CheckedAt_idx" ON public."DataQualityReports_2026_11" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_11_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_11_Status_idx" ON public."DataQualityReports_2026_11" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_11_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_11_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_11" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2026_12_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_12_CheckedAt_idx" ON public."DataQualityReports_2026_12" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2026_12_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2026_12_Status_idx" ON public."DataQualityReports_2026_12" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2026_12_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2026_12_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2026_12" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_01_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_01_CheckedAt_idx" ON public."DataQualityReports_2027_01" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_01_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_01_Status_idx" ON public."DataQualityReports_2027_01" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_01_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_01_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_01" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_02_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_02_CheckedAt_idx" ON public."DataQualityReports_2027_02" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_02_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_02_Status_idx" ON public."DataQualityReports_2027_02" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_02_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_02_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_02" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_03_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_03_CheckedAt_idx" ON public."DataQualityReports_2027_03" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_03_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_03_Status_idx" ON public."DataQualityReports_2027_03" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_03_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_03_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_03" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_04_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_04_CheckedAt_idx" ON public."DataQualityReports_2027_04" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_04_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_04_Status_idx" ON public."DataQualityReports_2027_04" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_04_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_04_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_04" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_05_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_05_CheckedAt_idx" ON public."DataQualityReports_2027_05" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_05_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_05_Status_idx" ON public."DataQualityReports_2027_05" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_05_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_05_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_05" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_06_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_06_CheckedAt_idx" ON public."DataQualityReports_2027_06" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_06_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_06_Status_idx" ON public."DataQualityReports_2027_06" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_06_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_06_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_06" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_07_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_07_CheckedAt_idx" ON public."DataQualityReports_2027_07" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_07_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_07_Status_idx" ON public."DataQualityReports_2027_07" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_07_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_07_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_07" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_08_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_08_CheckedAt_idx" ON public."DataQualityReports_2027_08" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_08_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_08_Status_idx" ON public."DataQualityReports_2027_08" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_08_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_08_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_08" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_09_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_09_CheckedAt_idx" ON public."DataQualityReports_2027_09" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_09_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_09_Status_idx" ON public."DataQualityReports_2027_09" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_09_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_09_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_09" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_10_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_10_CheckedAt_idx" ON public."DataQualityReports_2027_10" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_10_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_10_Status_idx" ON public."DataQualityReports_2027_10" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_10_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_10_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_10" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_11_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_11_CheckedAt_idx" ON public."DataQualityReports_2027_11" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_11_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_11_Status_idx" ON public."DataQualityReports_2027_11" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_11_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_11_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_11" USING btree ("Symbol", "PeriodMonth");


--
-- Name: DataQualityReports_2027_12_CheckedAt_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_12_CheckedAt_idx" ON public."DataQualityReports_2027_12" USING btree ("CheckedAt" DESC);


--
-- Name: DataQualityReports_2027_12_Status_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "DataQualityReports_2027_12_Status_idx" ON public."DataQualityReports_2027_12" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: DataQualityReports_2027_12_Symbol_PeriodMonth_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "DataQualityReports_2027_12_Symbol_PeriodMonth_idx" ON public."DataQualityReports_2027_12" USING btree ("Symbol", "PeriodMonth");


--
-- Name: IX_DirtyMinutes_OpenTime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_DirtyMinutes_OpenTime" ON public."DirtyMinutes" USING btree ("OpenTime");


--
-- Name: IX_HistoricalAudit_Watermarks_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_HistoricalAudit_Watermarks_Status" ON public."HistoricalAudit_Watermarks" USING btree ("Status");


--
-- Name: IX_Ohlcv_1min_ProcessingStatus_OpenTime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_Ohlcv_1min_ProcessingStatus_OpenTime" ON ONLY public."Ohlcv_1min" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: IX_OrderBook_Features_OpenTime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_OrderBook_Features_OpenTime" ON ONLY public."OrderBook_Features" USING btree ("OpenTime");


--
-- Name: IX_TrackedSymbols_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TrackedSymbols_IsActive" ON public."TrackedSymbols" USING btree ("IsActive");


--
-- Name: Ohlcv_1min_2026_01_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_01_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_01" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_02_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_02_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_02" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_03_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_03_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_03" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_04_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_04_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_04" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_05_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_05_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_05" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_06_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_06_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_06" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_07_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_07_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_07" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_08_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_08_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_08" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_09_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_09_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_09" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_10_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_10_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_10" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_11_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_11_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_11" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2026_12_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2026_12_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2026_12" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_01_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_01_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_01" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_02_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_02_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_02" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_03_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_03_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_03" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_04_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_04_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_04" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_05_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_05_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_05" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_06_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_06_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_06" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_07_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_07_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_07" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_08_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_08_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_08" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_09_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_09_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_09" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_10_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_10_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_10" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_11_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_11_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_11" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: Ohlcv_1min_2027_12_ProcessingStatus_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Ohlcv_1min_2027_12_ProcessingStatus_OpenTime_idx" ON public."Ohlcv_1min_2027_12" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: OrderBook_Features_2026_01_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_01_OpenTime_idx" ON public."OrderBook_Features_2026_01" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_02_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_02_OpenTime_idx" ON public."OrderBook_Features_2026_02" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_03_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_03_OpenTime_idx" ON public."OrderBook_Features_2026_03" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_04_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_04_OpenTime_idx" ON public."OrderBook_Features_2026_04" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_05_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_05_OpenTime_idx" ON public."OrderBook_Features_2026_05" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_06_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_06_OpenTime_idx" ON public."OrderBook_Features_2026_06" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_07_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_07_OpenTime_idx" ON public."OrderBook_Features_2026_07" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_08_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_08_OpenTime_idx" ON public."OrderBook_Features_2026_08" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_09_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_09_OpenTime_idx" ON public."OrderBook_Features_2026_09" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_10_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_10_OpenTime_idx" ON public."OrderBook_Features_2026_10" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_11_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_11_OpenTime_idx" ON public."OrderBook_Features_2026_11" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2026_12_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2026_12_OpenTime_idx" ON public."OrderBook_Features_2026_12" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_01_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_01_OpenTime_idx" ON public."OrderBook_Features_2027_01" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_02_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_02_OpenTime_idx" ON public."OrderBook_Features_2027_02" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_03_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_03_OpenTime_idx" ON public."OrderBook_Features_2027_03" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_04_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_04_OpenTime_idx" ON public."OrderBook_Features_2027_04" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_05_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_05_OpenTime_idx" ON public."OrderBook_Features_2027_05" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_06_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_06_OpenTime_idx" ON public."OrderBook_Features_2027_06" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_07_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_07_OpenTime_idx" ON public."OrderBook_Features_2027_07" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_08_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_08_OpenTime_idx" ON public."OrderBook_Features_2027_08" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_09_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_09_OpenTime_idx" ON public."OrderBook_Features_2027_09" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_10_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_10_OpenTime_idx" ON public."OrderBook_Features_2027_10" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_11_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_11_OpenTime_idx" ON public."OrderBook_Features_2027_11" USING btree ("OpenTime");


--
-- Name: OrderBook_Features_2027_12_OpenTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "OrderBook_Features_2027_12_OpenTime_idx" ON public."OrderBook_Features_2027_12" USING btree ("OpenTime");


--
-- Name: ix_trades_symbol_tradetime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trades_symbol_tradetime ON ONLY public."Trades" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_01_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_01_Symbol_TradeTime_idx" ON public."Trades_2026_01" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_02_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_02_Symbol_TradeTime_idx" ON public."Trades_2026_02" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_03_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_03_Symbol_TradeTime_idx" ON public."Trades_2026_03" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_04_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_04_Symbol_TradeTime_idx" ON public."Trades_2026_04" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_05_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_05_Symbol_TradeTime_idx" ON public."Trades_2026_05" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_06_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_06_Symbol_TradeTime_idx" ON public."Trades_2026_06" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_07_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_07_Symbol_TradeTime_idx" ON public."Trades_2026_07" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_08_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_08_Symbol_TradeTime_idx" ON public."Trades_2026_08" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_09_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_09_Symbol_TradeTime_idx" ON public."Trades_2026_09" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_10_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_10_Symbol_TradeTime_idx" ON public."Trades_2026_10" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_11_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_11_Symbol_TradeTime_idx" ON public."Trades_2026_11" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_12_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_12_Symbol_TradeTime_idx" ON public."Trades_2026_12" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_01_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_01_Symbol_TradeTime_idx" ON public."Trades_2027_01" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_02_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_02_Symbol_TradeTime_idx" ON public."Trades_2027_02" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_03_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_03_Symbol_TradeTime_idx" ON public."Trades_2027_03" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_04_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_04_Symbol_TradeTime_idx" ON public."Trades_2027_04" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_05_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_05_Symbol_TradeTime_idx" ON public."Trades_2027_05" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_06_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_06_Symbol_TradeTime_idx" ON public."Trades_2027_06" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_07_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_07_Symbol_TradeTime_idx" ON public."Trades_2027_07" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_08_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_08_Symbol_TradeTime_idx" ON public."Trades_2027_08" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_09_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_09_Symbol_TradeTime_idx" ON public."Trades_2027_09" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_10_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_10_Symbol_TradeTime_idx" ON public."Trades_2027_10" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_11_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_11_Symbol_TradeTime_idx" ON public."Trades_2027_11" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2027_12_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2027_12_Symbol_TradeTime_idx" ON public."Trades_2027_12" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: DataQualityFindings_2026_01_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_01_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_01_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_01_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_01_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_01_Severity_idx";


--
-- Name: DataQualityFindings_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_01_pkey";


--
-- Name: DataQualityFindings_2026_02_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_02_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_02_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_02_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_02_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_02_Severity_idx";


--
-- Name: DataQualityFindings_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_02_pkey";


--
-- Name: DataQualityFindings_2026_03_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_03_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_03_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_03_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_03_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_03_Severity_idx";


--
-- Name: DataQualityFindings_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_03_pkey";


--
-- Name: DataQualityFindings_2026_04_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_04_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_04_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_04_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_04_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_04_Severity_idx";


--
-- Name: DataQualityFindings_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_04_pkey";


--
-- Name: DataQualityFindings_2026_05_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_05_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_05_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_05_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_05_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_05_Severity_idx";


--
-- Name: DataQualityFindings_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_05_pkey";


--
-- Name: DataQualityFindings_2026_06_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_06_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_06_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_06_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_06_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_06_Severity_idx";


--
-- Name: DataQualityFindings_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_06_pkey";


--
-- Name: DataQualityFindings_2026_07_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_07_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_07_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_07_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_07_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_07_Severity_idx";


--
-- Name: DataQualityFindings_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_07_pkey";


--
-- Name: DataQualityFindings_2026_08_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_08_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_08_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_08_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_08_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_08_Severity_idx";


--
-- Name: DataQualityFindings_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_08_pkey";


--
-- Name: DataQualityFindings_2026_09_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_09_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_09_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_09_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_09_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_09_Severity_idx";


--
-- Name: DataQualityFindings_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_09_pkey";


--
-- Name: DataQualityFindings_2026_10_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_10_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_10_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_10_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_10_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_10_Severity_idx";


--
-- Name: DataQualityFindings_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_10_pkey";


--
-- Name: DataQualityFindings_2026_11_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_11_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_11_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_11_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_11_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_11_Severity_idx";


--
-- Name: DataQualityFindings_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_11_pkey";


--
-- Name: DataQualityFindings_2026_12_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2026_12_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2026_12_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2026_12_CheckedAt_idx";


--
-- Name: DataQualityFindings_2026_12_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2026_12_Severity_idx";


--
-- Name: DataQualityFindings_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2026_12_pkey";


--
-- Name: DataQualityFindings_2027_01_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_01_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_01_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_01_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_01_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_01_Severity_idx";


--
-- Name: DataQualityFindings_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_01_pkey";


--
-- Name: DataQualityFindings_2027_02_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_02_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_02_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_02_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_02_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_02_Severity_idx";


--
-- Name: DataQualityFindings_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_02_pkey";


--
-- Name: DataQualityFindings_2027_03_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_03_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_03_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_03_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_03_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_03_Severity_idx";


--
-- Name: DataQualityFindings_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_03_pkey";


--
-- Name: DataQualityFindings_2027_04_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_04_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_04_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_04_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_04_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_04_Severity_idx";


--
-- Name: DataQualityFindings_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_04_pkey";


--
-- Name: DataQualityFindings_2027_05_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_05_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_05_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_05_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_05_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_05_Severity_idx";


--
-- Name: DataQualityFindings_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_05_pkey";


--
-- Name: DataQualityFindings_2027_06_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_06_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_06_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_06_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_06_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_06_Severity_idx";


--
-- Name: DataQualityFindings_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_06_pkey";


--
-- Name: DataQualityFindings_2027_07_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_07_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_07_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_07_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_07_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_07_Severity_idx";


--
-- Name: DataQualityFindings_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_07_pkey";


--
-- Name: DataQualityFindings_2027_08_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_08_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_08_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_08_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_08_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_08_Severity_idx";


--
-- Name: DataQualityFindings_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_08_pkey";


--
-- Name: DataQualityFindings_2027_09_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_09_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_09_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_09_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_09_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_09_Severity_idx";


--
-- Name: DataQualityFindings_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_09_pkey";


--
-- Name: DataQualityFindings_2027_10_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_10_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_10_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_10_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_10_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_10_Severity_idx";


--
-- Name: DataQualityFindings_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_10_pkey";


--
-- Name: DataQualityFindings_2027_11_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_11_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_11_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_11_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_11_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_11_Severity_idx";


--
-- Name: DataQualityFindings_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_11_pkey";


--
-- Name: DataQualityFindings_2027_12_CheckGroup_Symbol_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_group_symbol ATTACH PARTITION public."DataQualityFindings_2027_12_CheckGroup_Symbol_idx";


--
-- Name: DataQualityFindings_2027_12_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_checked_at ATTACH PARTITION public."DataQualityFindings_2027_12_CheckedAt_idx";


--
-- Name: DataQualityFindings_2027_12_Severity_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqf_severity ATTACH PARTITION public."DataQualityFindings_2027_12_Severity_idx";


--
-- Name: DataQualityFindings_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityFindings_pkey" ATTACH PARTITION public."DataQualityFindings_2027_12_pkey";


--
-- Name: DataQualityReports_2026_01_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_01_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_01_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_01_Status_idx";


--
-- Name: DataQualityReports_2026_01_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_01_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_01_pkey";


--
-- Name: DataQualityReports_2026_02_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_02_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_02_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_02_Status_idx";


--
-- Name: DataQualityReports_2026_02_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_02_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_02_pkey";


--
-- Name: DataQualityReports_2026_03_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_03_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_03_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_03_Status_idx";


--
-- Name: DataQualityReports_2026_03_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_03_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_03_pkey";


--
-- Name: DataQualityReports_2026_04_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_04_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_04_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_04_Status_idx";


--
-- Name: DataQualityReports_2026_04_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_04_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_04_pkey";


--
-- Name: DataQualityReports_2026_05_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_05_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_05_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_05_Status_idx";


--
-- Name: DataQualityReports_2026_05_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_05_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_05_pkey";


--
-- Name: DataQualityReports_2026_06_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_06_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_06_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_06_Status_idx";


--
-- Name: DataQualityReports_2026_06_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_06_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_06_pkey";


--
-- Name: DataQualityReports_2026_07_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_07_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_07_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_07_Status_idx";


--
-- Name: DataQualityReports_2026_07_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_07_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_07_pkey";


--
-- Name: DataQualityReports_2026_08_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_08_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_08_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_08_Status_idx";


--
-- Name: DataQualityReports_2026_08_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_08_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_08_pkey";


--
-- Name: DataQualityReports_2026_09_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_09_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_09_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_09_Status_idx";


--
-- Name: DataQualityReports_2026_09_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_09_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_09_pkey";


--
-- Name: DataQualityReports_2026_10_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_10_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_10_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_10_Status_idx";


--
-- Name: DataQualityReports_2026_10_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_10_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_10_pkey";


--
-- Name: DataQualityReports_2026_11_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_11_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_11_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_11_Status_idx";


--
-- Name: DataQualityReports_2026_11_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_11_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_11_pkey";


--
-- Name: DataQualityReports_2026_12_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2026_12_CheckedAt_idx";


--
-- Name: DataQualityReports_2026_12_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2026_12_Status_idx";


--
-- Name: DataQualityReports_2026_12_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2026_12_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2026_12_pkey";


--
-- Name: DataQualityReports_2027_01_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_01_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_01_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_01_Status_idx";


--
-- Name: DataQualityReports_2027_01_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_01_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_01_pkey";


--
-- Name: DataQualityReports_2027_02_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_02_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_02_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_02_Status_idx";


--
-- Name: DataQualityReports_2027_02_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_02_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_02_pkey";


--
-- Name: DataQualityReports_2027_03_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_03_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_03_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_03_Status_idx";


--
-- Name: DataQualityReports_2027_03_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_03_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_03_pkey";


--
-- Name: DataQualityReports_2027_04_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_04_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_04_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_04_Status_idx";


--
-- Name: DataQualityReports_2027_04_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_04_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_04_pkey";


--
-- Name: DataQualityReports_2027_05_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_05_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_05_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_05_Status_idx";


--
-- Name: DataQualityReports_2027_05_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_05_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_05_pkey";


--
-- Name: DataQualityReports_2027_06_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_06_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_06_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_06_Status_idx";


--
-- Name: DataQualityReports_2027_06_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_06_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_06_pkey";


--
-- Name: DataQualityReports_2027_07_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_07_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_07_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_07_Status_idx";


--
-- Name: DataQualityReports_2027_07_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_07_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_07_pkey";


--
-- Name: DataQualityReports_2027_08_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_08_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_08_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_08_Status_idx";


--
-- Name: DataQualityReports_2027_08_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_08_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_08_pkey";


--
-- Name: DataQualityReports_2027_09_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_09_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_09_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_09_Status_idx";


--
-- Name: DataQualityReports_2027_09_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_09_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_09_pkey";


--
-- Name: DataQualityReports_2027_10_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_10_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_10_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_10_Status_idx";


--
-- Name: DataQualityReports_2027_10_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_10_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_10_pkey";


--
-- Name: DataQualityReports_2027_11_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_11_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_11_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_11_Status_idx";


--
-- Name: DataQualityReports_2027_11_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_11_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_11_pkey";


--
-- Name: DataQualityReports_2027_12_CheckedAt_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_checked_at ATTACH PARTITION public."DataQualityReports_2027_12_CheckedAt_idx";


--
-- Name: DataQualityReports_2027_12_Status_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_status ATTACH PARTITION public."DataQualityReports_2027_12_Status_idx";


--
-- Name: DataQualityReports_2027_12_Symbol_PeriodMonth_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_dqr_symbol_month ATTACH PARTITION public."DataQualityReports_2027_12_Symbol_PeriodMonth_idx";


--
-- Name: DataQualityReports_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."DataQualityReports_pkey" ATTACH PARTITION public."DataQualityReports_2027_12_pkey";


--
-- Name: Ohlcv_1min_2026_01_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_01_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_01_pkey";


--
-- Name: Ohlcv_1min_2026_02_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_02_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_02_pkey";


--
-- Name: Ohlcv_1min_2026_03_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_03_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_03_pkey";


--
-- Name: Ohlcv_1min_2026_04_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_04_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_04_pkey";


--
-- Name: Ohlcv_1min_2026_05_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_05_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_05_pkey";


--
-- Name: Ohlcv_1min_2026_06_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_06_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_06_pkey";


--
-- Name: Ohlcv_1min_2026_07_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_07_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_07_pkey";


--
-- Name: Ohlcv_1min_2026_08_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_08_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_08_pkey";


--
-- Name: Ohlcv_1min_2026_09_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_09_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_09_pkey";


--
-- Name: Ohlcv_1min_2026_10_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_10_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_10_pkey";


--
-- Name: Ohlcv_1min_2026_11_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_11_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_11_pkey";


--
-- Name: Ohlcv_1min_2026_12_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2026_12_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2026_12_pkey";


--
-- Name: Ohlcv_1min_2027_01_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_01_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_01_pkey";


--
-- Name: Ohlcv_1min_2027_02_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_02_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_02_pkey";


--
-- Name: Ohlcv_1min_2027_03_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_03_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_03_pkey";


--
-- Name: Ohlcv_1min_2027_04_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_04_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_04_pkey";


--
-- Name: Ohlcv_1min_2027_05_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_05_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_05_pkey";


--
-- Name: Ohlcv_1min_2027_06_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_06_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_06_pkey";


--
-- Name: Ohlcv_1min_2027_07_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_07_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_07_pkey";


--
-- Name: Ohlcv_1min_2027_08_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_08_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_08_pkey";


--
-- Name: Ohlcv_1min_2027_09_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_09_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_09_pkey";


--
-- Name: Ohlcv_1min_2027_10_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_10_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_10_pkey";


--
-- Name: Ohlcv_1min_2027_11_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_11_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_11_pkey";


--
-- Name: Ohlcv_1min_2027_12_ProcessingStatus_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_Ohlcv_1min_ProcessingStatus_OpenTime" ATTACH PARTITION public."Ohlcv_1min_2027_12_ProcessingStatus_OpenTime_idx";


--
-- Name: Ohlcv_1min_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_Ohlcv_1min" ATTACH PARTITION public."Ohlcv_1min_2027_12_pkey";


--
-- Name: Ohlcv_Features_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_01_pkey";


--
-- Name: Ohlcv_Features_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_02_pkey";


--
-- Name: Ohlcv_Features_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_03_pkey";


--
-- Name: Ohlcv_Features_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_04_pkey";


--
-- Name: Ohlcv_Features_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_05_pkey";


--
-- Name: Ohlcv_Features_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_06_pkey";


--
-- Name: Ohlcv_Features_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_07_pkey";


--
-- Name: Ohlcv_Features_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_08_pkey";


--
-- Name: Ohlcv_Features_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_09_pkey";


--
-- Name: Ohlcv_Features_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_10_pkey";


--
-- Name: Ohlcv_Features_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_11_pkey";


--
-- Name: Ohlcv_Features_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2026_12_pkey";


--
-- Name: Ohlcv_Features_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_01_pkey";


--
-- Name: Ohlcv_Features_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_02_pkey";


--
-- Name: Ohlcv_Features_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_03_pkey";


--
-- Name: Ohlcv_Features_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_04_pkey";


--
-- Name: Ohlcv_Features_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_05_pkey";


--
-- Name: Ohlcv_Features_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_06_pkey";


--
-- Name: Ohlcv_Features_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_07_pkey";


--
-- Name: Ohlcv_Features_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_08_pkey";


--
-- Name: Ohlcv_Features_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_09_pkey";


--
-- Name: Ohlcv_Features_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_10_pkey";


--
-- Name: Ohlcv_Features_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_11_pkey";


--
-- Name: Ohlcv_Features_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Ohlcv_Features_pkey" ATTACH PARTITION public."Ohlcv_Features_2027_12_pkey";


--
-- Name: OrderBook_Features_2026_01_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_01_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_01_pkey";


--
-- Name: OrderBook_Features_2026_02_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_02_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_02_pkey";


--
-- Name: OrderBook_Features_2026_03_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_03_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_03_pkey";


--
-- Name: OrderBook_Features_2026_04_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_04_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_04_pkey";


--
-- Name: OrderBook_Features_2026_05_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_05_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_05_pkey";


--
-- Name: OrderBook_Features_2026_06_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_06_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_06_pkey";


--
-- Name: OrderBook_Features_2026_07_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_07_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_07_pkey";


--
-- Name: OrderBook_Features_2026_08_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_08_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_08_pkey";


--
-- Name: OrderBook_Features_2026_09_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_09_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_09_pkey";


--
-- Name: OrderBook_Features_2026_10_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_10_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_10_pkey";


--
-- Name: OrderBook_Features_2026_11_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_11_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_11_pkey";


--
-- Name: OrderBook_Features_2026_12_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2026_12_OpenTime_idx";


--
-- Name: OrderBook_Features_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2026_12_pkey";


--
-- Name: OrderBook_Features_2027_01_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_01_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_01_pkey";


--
-- Name: OrderBook_Features_2027_02_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_02_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_02_pkey";


--
-- Name: OrderBook_Features_2027_03_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_03_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_03_pkey";


--
-- Name: OrderBook_Features_2027_04_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_04_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_04_pkey";


--
-- Name: OrderBook_Features_2027_05_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_05_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_05_pkey";


--
-- Name: OrderBook_Features_2027_06_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_06_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_06_pkey";


--
-- Name: OrderBook_Features_2027_07_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_07_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_07_pkey";


--
-- Name: OrderBook_Features_2027_08_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_08_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_08_pkey";


--
-- Name: OrderBook_Features_2027_09_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_09_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_09_pkey";


--
-- Name: OrderBook_Features_2027_10_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_10_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_10_pkey";


--
-- Name: OrderBook_Features_2027_11_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_11_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_11_pkey";


--
-- Name: OrderBook_Features_2027_12_OpenTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."IX_OrderBook_Features_OpenTime" ATTACH PARTITION public."OrderBook_Features_2027_12_OpenTime_idx";


--
-- Name: OrderBook_Features_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."PK_OrderBook_Features" ATTACH PARTITION public."OrderBook_Features_2027_12_pkey";


--
-- Name: Trades_2026_01_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_01_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_01_pkey";


--
-- Name: Trades_2026_02_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_02_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_02_pkey";


--
-- Name: Trades_2026_03_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_03_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_03_pkey";


--
-- Name: Trades_2026_04_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_04_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_04_pkey";


--
-- Name: Trades_2026_05_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_05_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_05_pkey";


--
-- Name: Trades_2026_06_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_06_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_06_pkey";


--
-- Name: Trades_2026_07_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_07_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_07_pkey";


--
-- Name: Trades_2026_08_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_08_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_08_pkey";


--
-- Name: Trades_2026_09_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_09_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_09_pkey";


--
-- Name: Trades_2026_10_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_10_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_10_pkey";


--
-- Name: Trades_2026_11_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_11_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_11_pkey";


--
-- Name: Trades_2026_12_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_12_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_12_pkey";


--
-- Name: Trades_2027_01_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_01_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_01_pkey";


--
-- Name: Trades_2027_02_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_02_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_02_pkey";


--
-- Name: Trades_2027_03_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_03_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_03_pkey";


--
-- Name: Trades_2027_04_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_04_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_04_pkey";


--
-- Name: Trades_2027_05_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_05_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_05_pkey";


--
-- Name: Trades_2027_06_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_06_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_06_pkey";


--
-- Name: Trades_2027_07_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_07_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_07_pkey";


--
-- Name: Trades_2027_08_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_08_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_08_pkey";


--
-- Name: Trades_2027_09_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_09_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_09_pkey";


--
-- Name: Trades_2027_10_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_10_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_10_pkey";


--
-- Name: Trades_2027_11_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_11_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_11_pkey";


--
-- Name: Trades_2027_12_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2027_12_Symbol_TradeTime_idx";


--
-- Name: Trades_2027_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2027_12_pkey";


--
-- Отметка времени захвата свечи расчётом фич (миграция 011): протухший захват снова
-- становится кандидатом, свечи убитого воркера и упавших символов возвращаются в работу.
-- ALTER стоит после всех ATTACH и распространяется на все партиции; NULL — «не захвачена».
--

ALTER TABLE public."Ohlcv_1min"
    ADD COLUMN "ClaimedAt" timestamp with time zone;


--
-- CVD-дельта минуты (миграция 012): считается агрегацией в том же проходе по тикам,
-- что и свеча. NULL — «дельта не посчитана» (свеча до миграции или из klines API);
-- читающая сторона в этом случае откатывается на запрос по тикам.
--

ALTER TABLE public."Ohlcv_1min"
    ADD COLUMN "CvdDelta" numeric(28,8);


--
-- PostgreSQL database dump complete
--


