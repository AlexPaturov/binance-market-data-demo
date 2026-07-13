-- Schema baseline for the "market_analytics" database.
-- Auto-generated with: pg_dump --schema-only --no-owner --no-privileges
-- Source: local development DB (partitioned, PostgreSQL 16), captured 2026-07-11.
-- Applied automatically on a fresh volume via docker-entrypoint-initdb.d (docker/postgres/init).

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
-- Name: sp_aggregate_trades_to_ohlcv(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_aggregate_trades_to_ohlcv() RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
    interval BIGINT := 60000;
BEGIN
    -- 1. Получаем "вотермарку" - нашу точку старта
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'OhlcvAggregator';

    -- 2. Находим время последней НОВОЙ сделки, чтобы определить конец "окна"
    SELECT MAX("TradeTime") INTO end_timestamp
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    -- 3. Агрегируем только "новые" тики в найденном "окне"
    CREATE TEMP TABLE NewCandles ON COMMIT DROP AS
    WITH Aggregates AS (
        SELECT "Symbol", ("TradeTime" / interval) * interval AS "OpenTime", MIN("Price") AS "LowPrice", MAX("Price") AS "HighPrice",
               SUM("Quantity") AS "Volume", MIN("TradeId") AS "FirstTradeId", MAX("TradeId") AS "LastTradeId"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp AND "TradeTime" <= end_timestamp
        GROUP BY 1, 2
    )
    SELECT agg."Symbol", agg."OpenTime", f."Price" AS "OpenPrice", agg."HighPrice", agg."LowPrice", l."Price" AS "ClosePrice", agg."Volume"
    FROM Aggregates agg
    JOIN public."Trades" f ON agg."FirstTradeId" = f."TradeId"
    JOIN public."Trades" l ON agg."LastTradeId" = l."TradeId";

    IF NOT FOUND THEN RETURN; END IF;

    -- 4. Вставляем/обновляем свечи
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume")
    SELECT "Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume" FROM NewCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = EXCLUDED."Volume";

    -- 5. Помечаем обработанные тики как "processed"
    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp AND "TradeTime" <= end_timestamp;

    -- 6. Сдвигаем "вотермарку" вперед
    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'OhlcvAggregator';
END;
$$;


--
-- Name: sp_aggregate_trades_to_ohlcv(bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_aggregate_trades_to_ohlcv(p_start_timestamp bigint, p_end_timestamp bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    interval_ms BIGINT := 60000;
BEGIN
    -- Делаем агрегацию и UPSERT в одном запросе
    WITH TradesInWindow AS (
        SELECT "Symbol", "TradeId", "Price", "Quantity", "TradeTime"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= p_start_timestamp
          AND "TradeTime" < p_end_timestamp
    ),
    MinuteAggregates AS (
        -- Группируем сделки по минутам
        SELECT
            "Symbol",
            ("TradeTime" / interval_ms) * interval_ms AS "OpenTime",
            MIN("Price") AS "Low",
            MAX("Price") AS "High",
            SUM("Quantity") AS "Vol",
            (array_agg("Price" ORDER BY "TradeTime" ASC, "TradeId" ASC))[1] AS "Open",
            (array_agg("Price" ORDER BY "TradeTime" DESC, "TradeId" DESC))[1] AS "Close"
        FROM TradesInWindow
        GROUP BY "Symbol", "OpenTime"
    )
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT 
        "Symbol", "OpenTime", "Open", "High", "Low", "Close", "Vol", 'new'
    FROM MinuteAggregates
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        -- Если свеча уже существует, мы МЕРДЖИМ ее с новыми данными
        "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice", -- Цена закрытия всегда последняя
        "Volume" = public."Ohlcv_1min"."Volume" + EXCLUDED."Volume", -- Объемы суммируются
        "ProcessingStatus" = 'new';

    -- Помечаем обработанные тики как "processed"
    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE 
        "ProcessingStatus" = 'new'
        AND "TradeTime" >= p_start_timestamp
        AND "TradeTime" < p_end_timestamp;
END;
$$;


--
-- Name: sp_bulk_insert_trades(bigint[], character varying[], numeric[], numeric[], numeric[], bigint[], boolean[], boolean[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_bulk_insert_trades(p_trade_ids bigint[], p_symbols character varying[], p_prices numeric[], p_quantities numeric[], p_quote_quantities numeric[], p_trade_times bigint[], p_is_buyer_makers boolean[], p_is_best_matches boolean[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO public."Trades" (
        "TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity",
        "TradeTime", "IsBuyerMaker", "IsBestMatch"
    )
    SELECT * FROM UNNEST(
        p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities,
        p_trade_times, p_is_buyer_makers, p_is_best_matches
    )
    ON CONFLICT ("TradeId", "Symbol", "TradeTime") DO NOTHING;
END;
$$;


--
-- Name: sp_ensure_trades_partition(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_ensure_trades_partition(target_time bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    month_start TIMESTAMP;
    month_end   TIMESTAMP;
    part_name   TEXT;
    from_ms     BIGINT;
    to_ms       BIGINT;
BEGIN
    month_start := DATE_TRUNC('month', TO_TIMESTAMP(target_time / 1000.0) AT TIME ZONE 'UTC');
    month_end   := month_start + INTERVAL '1 month';
    part_name   := 'Trades_' || TO_CHAR(month_start, 'YYYY_MM');
    from_ms     := EXTRACT(EPOCH FROM month_start)::BIGINT * 1000;
    to_ms       := EXTRACT(EPOCH FROM month_end)::BIGINT * 1000;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = part_name AND n.nspname = 'public') THEN
        EXECUTE format(
            'CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
            part_name, from_ms, to_ms
        );
    END IF;
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
-- Name: sp_process_features(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_process_features() RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
BEGIN
    -- 1. Получаем "вотермарку" для этого процесса
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'FeatureCalculator';

    -- 2. Находим конец "окна" обработки - последнюю новую свечу
    SELECT MAX("OpenTime") INTO end_timestamp
    FROM public."Ohlcv_1min"
    WHERE "ProcessingStatus" = 'new' AND "OpenTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    -- 3. Помечаем свечи в нашем "окне" как обработанные
    -- Мы делаем это в начале, чтобы избежать повторной обработки в параллельных воркерах (задел на будущее)
    UPDATE public."Ohlcv_1min"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new'
      AND "OpenTime" >= start_timestamp
      AND "OpenTime" <= end_timestamp;

    -- 4. Сдвигаем "вотермарку"
    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'FeatureCalculator';

END;
$$;


--
-- Name: sp_rotate_trades_partition(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_rotate_trades_partition() RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    current_month TIMESTAMP;
    target_month  TIMESTAMP;
    part_name     TEXT;
    from_ms       BIGINT;
    to_ms         BIGINT;
    old_month     TIMESTAMP;
    old_part_name TEXT;
BEGIN
    current_month := DATE_TRUNC('month', NOW() AT TIME ZONE 'UTC');

    FOR target_month IN
        SELECT m FROM generate_series(current_month, current_month + INTERVAL '1 month', INTERVAL '1 month') AS m
    LOOP
        part_name := 'Trades_' || TO_CHAR(target_month, 'YYYY_MM');
        from_ms   := EXTRACT(EPOCH FROM target_month)::BIGINT * 1000;
        to_ms     := EXTRACT(EPOCH FROM (target_month + INTERVAL '1 month'))::BIGINT * 1000;

        IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                       WHERE c.relname = part_name AND n.nspname = 'public') THEN
            EXECUTE format(
                'CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
                part_name, from_ms, to_ms
            );
        END IF;
    END LOOP;

    old_month     := current_month - INTERVAL '13 months';
    old_part_name := 'Trades_' || TO_CHAR(old_month, 'YYYY_MM');

    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = old_part_name AND n.nspname = 'public') THEN
        EXECUTE format('ALTER TABLE public."Trades" DETACH PARTITION public.%I', old_part_name);
        EXECUTE format('DROP TABLE public.%I', old_part_name);
        RAISE NOTICE 'Dropped old partition: %', old_part_name;
    END IF;
END;
$$;


--
-- Name: sp_update_tracked_symbols(character varying[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_update_tracked_symbols(p_symbols character varying[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE public."TrackedSymbols" SET "IsActive" = FALSE WHERE "IsActive" = TRUE AND "Symbol" <> ALL(p_symbols);
    INSERT INTO public."TrackedSymbols" ("Symbol", "IsActive", "LastScanned")
    SELECT symbol, TRUE, NOW() FROM UNNEST(p_symbols) AS u(symbol)
    ON CONFLICT ("Symbol") DO UPDATE SET "IsActive" = TRUE, "LastScanned" = NOW();
END;
$$;


--
-- Name: sp_upsert_ohlcv_features(character varying[], bigint[], numeric[], numeric[], numeric[], numeric[], numeric[], numeric[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sp_upsert_ohlcv_features(p_symbols character varying[], p_open_times bigint[], p_rsi_14 numeric[], p_macd_signals numeric[], p_macd_hists numeric[], p_ma_1051200 numeric[], p_ma_201600 numeric[], p_cvds numeric[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    CREATE TEMP TABLE NewFeatures ON COMMIT DROP AS
    SELECT * FROM UNNEST(
        p_symbols, p_open_times, p_rsi_14, p_macd_signals, p_macd_hists,
        p_ma_1051200, p_ma_201600, p_cvds
    ) AS t(
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist",
        "MA_1051200", "MA_201600", "CVD"
    );

    INSERT INTO public."Ohlcv_Features" (
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist",
        "MA_1051200", "MA_201600", "CVD"
    )
    SELECT * FROM NewFeatures
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET
        "RSI_14" = EXCLUDED."RSI_14",
        "MACD_Signal" = EXCLUDED."MACD_Signal",
        "MACD_Hist" = EXCLUDED."MACD_Hist",
        "MA_1051200" = EXCLUDED."MA_1051200",
        "MA_201600" = EXCLUDED."MA_201600",
        "CVD" = EXCLUDED."CVD";
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

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
);


--
-- Name: DataQualityReports_Id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public."DataQualityReports_Id_seq"
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: DataQualityReports_Id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public."DataQualityReports_Id_seq" OWNED BY public."DataQualityReports"."Id";


--
-- Name: DataQualityFindings; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DataQualityFindings" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY NOT NULL,
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
    "MA_1051200" numeric(18,8),
    "MA_201600" numeric(18,8),
    "CVD" numeric(28,8)
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
    "LastScanned" timestamp with time zone
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
)
PARTITION BY RANGE ("TradeTime");


--
-- Name: Trades_2025_01; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_01" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_02; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_02" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_03; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_03" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_04; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_04" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_05; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_05" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_06; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_06" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_07; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_07" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_08; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_08" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_09; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_09" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_10; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_10" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_11; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_11" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_12; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Trades_2025_12" (
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
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
    "IsMyTrade" boolean DEFAULT false NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


--
-- Name: Trades_2025_01; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_01" FOR VALUES FROM ('1735689600000') TO ('1738368000000');


--
-- Name: Trades_2025_02; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_02" FOR VALUES FROM ('1738368000000') TO ('1740787200000');


--
-- Name: Trades_2025_03; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_03" FOR VALUES FROM ('1740787200000') TO ('1743465600000');


--
-- Name: Trades_2025_04; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_04" FOR VALUES FROM ('1743465600000') TO ('1746057600000');


--
-- Name: Trades_2025_05; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_05" FOR VALUES FROM ('1746057600000') TO ('1748736000000');


--
-- Name: Trades_2025_06; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_06" FOR VALUES FROM ('1748736000000') TO ('1751328000000');


--
-- Name: Trades_2025_07; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_07" FOR VALUES FROM ('1751328000000') TO ('1754006400000');


--
-- Name: Trades_2025_08; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_08" FOR VALUES FROM ('1754006400000') TO ('1756684800000');


--
-- Name: Trades_2025_09; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_09" FOR VALUES FROM ('1756684800000') TO ('1759276800000');


--
-- Name: Trades_2025_10; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_10" FOR VALUES FROM ('1759276800000') TO ('1761955200000');


--
-- Name: Trades_2025_11; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_11" FOR VALUES FROM ('1761955200000') TO ('1764547200000');


--
-- Name: Trades_2025_12; Type: TABLE ATTACH; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades" ATTACH PARTITION public."Trades_2025_12" FOR VALUES FROM ('1764547200000') TO ('1767225600000');


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
-- Name: DataQualityReports Id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports" ALTER COLUMN "Id" SET DEFAULT nextval('public."DataQualityReports_Id_seq"'::regclass);


--
-- Name: DataQualityReports DataQualityReports_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityReports"
    ADD CONSTRAINT "DataQualityReports_pkey" PRIMARY KEY ("Id");


--
-- Name: DataQualityFindings DataQualityFindings_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DataQualityFindings"
    ADD CONSTRAINT "DataQualityFindings_pkey" PRIMARY KEY ("Id");


--
-- Name: HistoricalAudit_Watermarks HistoricalAudit_Watermarks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."HistoricalAudit_Watermarks"
    ADD CONSTRAINT "HistoricalAudit_Watermarks_pkey" PRIMARY KEY ("Symbol");


--
-- Name: Ohlcv_Features Ohlcv_Features_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_Features"
    ADD CONSTRAINT "Ohlcv_Features_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Ohlcv_1min PK_Ohlcv_1min; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Ohlcv_1min"
    ADD CONSTRAINT "PK_Ohlcv_1min" PRIMARY KEY ("Symbol", "OpenTime");


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
-- Name: Trades_2025_01 Trades_2025_01_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_01"
    ADD CONSTRAINT "Trades_2025_01_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_02 Trades_2025_02_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_02"
    ADD CONSTRAINT "Trades_2025_02_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_03 Trades_2025_03_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_03"
    ADD CONSTRAINT "Trades_2025_03_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_04 Trades_2025_04_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_04"
    ADD CONSTRAINT "Trades_2025_04_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_05 Trades_2025_05_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_05"
    ADD CONSTRAINT "Trades_2025_05_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_06 Trades_2025_06_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_06"
    ADD CONSTRAINT "Trades_2025_06_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_07 Trades_2025_07_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_07"
    ADD CONSTRAINT "Trades_2025_07_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_08 Trades_2025_08_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_08"
    ADD CONSTRAINT "Trades_2025_08_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_09 Trades_2025_09_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_09"
    ADD CONSTRAINT "Trades_2025_09_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_10 Trades_2025_10_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_10"
    ADD CONSTRAINT "Trades_2025_10_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_11 Trades_2025_11_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_11"
    ADD CONSTRAINT "Trades_2025_11_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


--
-- Name: Trades_2025_12 Trades_2025_12_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Trades_2025_12"
    ADD CONSTRAINT "Trades_2025_12_pkey" PRIMARY KEY ("TradeId", "Symbol", "TradeTime");


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
-- Name: IX_HistoricalAudit_Watermarks_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_HistoricalAudit_Watermarks_Status" ON public."HistoricalAudit_Watermarks" USING btree ("Status");


--
-- Name: IX_Ohlcv_1min_ProcessingStatus_OpenTime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_Ohlcv_1min_ProcessingStatus_OpenTime" ON public."Ohlcv_1min" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: IX_TrackedSymbols_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TrackedSymbols_IsActive" ON public."TrackedSymbols" USING btree ("IsActive");


--
-- Name: ix_trades_processingstatus; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trades_processingstatus ON ONLY public."Trades" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_01_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_01_ProcessingStatus_idx" ON public."Trades_2025_01" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: ix_trades_symbol_tradetime; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_trades_symbol_tradetime ON ONLY public."Trades" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_01_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_01_Symbol_TradeTime_idx" ON public."Trades_2025_01" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_02_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_02_ProcessingStatus_idx" ON public."Trades_2025_02" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_02_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_02_Symbol_TradeTime_idx" ON public."Trades_2025_02" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_03_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_03_ProcessingStatus_idx" ON public."Trades_2025_03" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_03_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_03_Symbol_TradeTime_idx" ON public."Trades_2025_03" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_04_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_04_ProcessingStatus_idx" ON public."Trades_2025_04" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_04_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_04_Symbol_TradeTime_idx" ON public."Trades_2025_04" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_05_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_05_ProcessingStatus_idx" ON public."Trades_2025_05" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_05_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_05_Symbol_TradeTime_idx" ON public."Trades_2025_05" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_06_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_06_ProcessingStatus_idx" ON public."Trades_2025_06" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_06_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_06_Symbol_TradeTime_idx" ON public."Trades_2025_06" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_07_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_07_ProcessingStatus_idx" ON public."Trades_2025_07" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_07_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_07_Symbol_TradeTime_idx" ON public."Trades_2025_07" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_08_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_08_ProcessingStatus_idx" ON public."Trades_2025_08" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_08_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_08_Symbol_TradeTime_idx" ON public."Trades_2025_08" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_09_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_09_ProcessingStatus_idx" ON public."Trades_2025_09" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_09_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_09_Symbol_TradeTime_idx" ON public."Trades_2025_09" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_10_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_10_ProcessingStatus_idx" ON public."Trades_2025_10" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_10_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_10_Symbol_TradeTime_idx" ON public."Trades_2025_10" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_11_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_11_ProcessingStatus_idx" ON public."Trades_2025_11" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_11_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_11_Symbol_TradeTime_idx" ON public."Trades_2025_11" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2025_12_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_12_ProcessingStatus_idx" ON public."Trades_2025_12" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2025_12_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2025_12_Symbol_TradeTime_idx" ON public."Trades_2025_12" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_01_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_01_ProcessingStatus_idx" ON public."Trades_2026_01" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_01_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_01_Symbol_TradeTime_idx" ON public."Trades_2026_01" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_02_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_02_ProcessingStatus_idx" ON public."Trades_2026_02" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_02_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_02_Symbol_TradeTime_idx" ON public."Trades_2026_02" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_03_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_03_ProcessingStatus_idx" ON public."Trades_2026_03" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_03_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_03_Symbol_TradeTime_idx" ON public."Trades_2026_03" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_04_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_04_ProcessingStatus_idx" ON public."Trades_2026_04" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_04_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_04_Symbol_TradeTime_idx" ON public."Trades_2026_04" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_05_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_05_ProcessingStatus_idx" ON public."Trades_2026_05" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_05_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_05_Symbol_TradeTime_idx" ON public."Trades_2026_05" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_06_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_06_ProcessingStatus_idx" ON public."Trades_2026_06" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_06_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_06_Symbol_TradeTime_idx" ON public."Trades_2026_06" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_07_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_07_ProcessingStatus_idx" ON public."Trades_2026_07" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_07_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_07_Symbol_TradeTime_idx" ON public."Trades_2026_07" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_08_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_08_ProcessingStatus_idx" ON public."Trades_2026_08" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_08_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_08_Symbol_TradeTime_idx" ON public."Trades_2026_08" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_09_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_09_ProcessingStatus_idx" ON public."Trades_2026_09" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_09_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_09_Symbol_TradeTime_idx" ON public."Trades_2026_09" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_10_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_10_ProcessingStatus_idx" ON public."Trades_2026_10" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_10_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_10_Symbol_TradeTime_idx" ON public."Trades_2026_10" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_11_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_11_ProcessingStatus_idx" ON public."Trades_2026_11" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_11_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_11_Symbol_TradeTime_idx" ON public."Trades_2026_11" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: Trades_2026_12_ProcessingStatus_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_12_ProcessingStatus_idx" ON public."Trades_2026_12" USING btree ("ProcessingStatus") WHERE (("ProcessingStatus")::text = 'new'::text);


--
-- Name: Trades_2026_12_Symbol_TradeTime_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "Trades_2026_12_Symbol_TradeTime_idx" ON public."Trades_2026_12" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: ix_dqf_checked_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_checked_at ON public."DataQualityFindings" USING btree ("CheckedAt" DESC);


--
-- Name: ix_dqf_severity; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_severity ON public."DataQualityFindings" USING btree ("Severity") WHERE (("Severity")::text <> 'ok'::text);


--
-- Name: ix_dqf_group_symbol; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqf_group_symbol ON public."DataQualityFindings" USING btree ("CheckGroup", "Symbol");


--
-- Name: ix_dqr_checked_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqr_checked_at ON public."DataQualityReports" USING btree ("CheckedAt" DESC);


--
-- Name: ix_dqr_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_dqr_status ON public."DataQualityReports" USING btree ("Status") WHERE (("Status")::text <> 'ok'::text);


--
-- Name: ix_dqr_symbol_month; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ix_dqr_symbol_month ON public."DataQualityReports" USING btree ("Symbol", "PeriodMonth");


--
-- Name: Trades_2025_01_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_01_ProcessingStatus_idx";


--
-- Name: Trades_2025_01_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_01_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_01_pkey";


--
-- Name: Trades_2025_02_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_02_ProcessingStatus_idx";


--
-- Name: Trades_2025_02_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_02_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_02_pkey";


--
-- Name: Trades_2025_03_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_03_ProcessingStatus_idx";


--
-- Name: Trades_2025_03_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_03_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_03_pkey";


--
-- Name: Trades_2025_04_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_04_ProcessingStatus_idx";


--
-- Name: Trades_2025_04_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_04_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_04_pkey";


--
-- Name: Trades_2025_05_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_05_ProcessingStatus_idx";


--
-- Name: Trades_2025_05_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_05_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_05_pkey";


--
-- Name: Trades_2025_06_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_06_ProcessingStatus_idx";


--
-- Name: Trades_2025_06_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_06_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_06_pkey";


--
-- Name: Trades_2025_07_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_07_ProcessingStatus_idx";


--
-- Name: Trades_2025_07_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_07_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_07_pkey";


--
-- Name: Trades_2025_08_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_08_ProcessingStatus_idx";


--
-- Name: Trades_2025_08_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_08_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_08_pkey";


--
-- Name: Trades_2025_09_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_09_ProcessingStatus_idx";


--
-- Name: Trades_2025_09_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_09_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_09_pkey";


--
-- Name: Trades_2025_10_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_10_ProcessingStatus_idx";


--
-- Name: Trades_2025_10_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_10_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_10_pkey";


--
-- Name: Trades_2025_11_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_11_ProcessingStatus_idx";


--
-- Name: Trades_2025_11_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_11_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_11_pkey";


--
-- Name: Trades_2025_12_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2025_12_ProcessingStatus_idx";


--
-- Name: Trades_2025_12_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2025_12_Symbol_TradeTime_idx";


--
-- Name: Trades_2025_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2025_12_pkey";


--
-- Name: Trades_2026_01_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_01_ProcessingStatus_idx";


--
-- Name: Trades_2026_01_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_01_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_01_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_01_pkey";


--
-- Name: Trades_2026_02_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_02_ProcessingStatus_idx";


--
-- Name: Trades_2026_02_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_02_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_02_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_02_pkey";


--
-- Name: Trades_2026_03_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_03_ProcessingStatus_idx";


--
-- Name: Trades_2026_03_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_03_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_03_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_03_pkey";


--
-- Name: Trades_2026_04_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_04_ProcessingStatus_idx";


--
-- Name: Trades_2026_04_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_04_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_04_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_04_pkey";


--
-- Name: Trades_2026_05_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_05_ProcessingStatus_idx";


--
-- Name: Trades_2026_05_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_05_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_05_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_05_pkey";


--
-- Name: Trades_2026_06_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_06_ProcessingStatus_idx";


--
-- Name: Trades_2026_06_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_06_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_06_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_06_pkey";


--
-- Name: Trades_2026_07_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_07_ProcessingStatus_idx";


--
-- Name: Trades_2026_07_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_07_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_07_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_07_pkey";


--
-- Name: Trades_2026_08_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_08_ProcessingStatus_idx";


--
-- Name: Trades_2026_08_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_08_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_08_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_08_pkey";


--
-- Name: Trades_2026_09_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_09_ProcessingStatus_idx";


--
-- Name: Trades_2026_09_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_09_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_09_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_09_pkey";


--
-- Name: Trades_2026_10_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_10_ProcessingStatus_idx";


--
-- Name: Trades_2026_10_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_10_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_10_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_10_pkey";


--
-- Name: Trades_2026_11_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_11_ProcessingStatus_idx";


--
-- Name: Trades_2026_11_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_11_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_11_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_11_pkey";


--
-- Name: Trades_2026_12_ProcessingStatus_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_processingstatus ATTACH PARTITION public."Trades_2026_12_ProcessingStatus_idx";


--
-- Name: Trades_2026_12_Symbol_TradeTime_idx; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public.ix_trades_symbol_tradetime ATTACH PARTITION public."Trades_2026_12_Symbol_TradeTime_idx";


--
-- Name: Trades_2026_12_pkey; Type: INDEX ATTACH; Schema: public; Owner: -
--

ALTER INDEX public."Trades_pkey" ATTACH PARTITION public."Trades_2026_12_pkey";


--
-- PostgreSQL database dump complete
--


