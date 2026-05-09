--
-- PostgreSQL database dump
--

-- Dumped from database version 16.10
-- Dumped by pg_dump version 16.10

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
-- Name: public; Type: SCHEMA; Schema: -; Owner: pg_database_owner
--

CREATE SCHEMA public;


ALTER SCHEMA public OWNER TO pg_database_owner;

--
-- Name: SCHEMA public; Type: COMMENT; Schema: -; Owner: pg_database_owner
--

COMMENT ON SCHEMA public IS 'standard public schema';


--
-- Name: sp_aggregate_trades_to_ohlcv(); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_aggregate_trades_to_ohlcv() OWNER TO bindatacoll;

--
-- Name: sp_aggregate_trades_to_ohlcv(bigint, bigint); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_aggregate_trades_to_ohlcv(p_start_timestamp bigint, p_end_timestamp bigint) OWNER TO bindatacoll;

--
-- Name: sp_bulk_insert_trades(bigint[], character varying[], numeric[], numeric[], numeric[], bigint[], boolean[], boolean[]); Type: FUNCTION; Schema: public; Owner: bindatacoll
--

CREATE FUNCTION public.sp_bulk_insert_trades(p_trade_ids bigint[], p_symbols character varying[], p_prices numeric[], p_quantities numeric[], p_quote_quantities numeric[], p_trade_times bigint[], p_is_buyer_makers boolean[], p_is_best_matches boolean[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO public."Trades" ("TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity", "TradeTime", "IsBuyerMaker", "IsBestMatch")
    SELECT * FROM UNNEST(p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities, p_trade_times, p_is_buyer_makers, p_is_best_matches)
    ON CONFLICT ("TradeId", "Symbol") DO NOTHING;
END;
$$;


ALTER FUNCTION public.sp_bulk_insert_trades(p_trade_ids bigint[], p_symbols character varying[], p_prices numeric[], p_quantities numeric[], p_quote_quantities numeric[], p_trade_times bigint[], p_is_buyer_makers boolean[], p_is_best_matches boolean[]) OWNER TO bindatacoll;

--
-- Name: sp_find_trade_id_gaps_in_window(text, bigint, bigint); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_find_trade_id_gaps_in_window(p_symbol text, p_start_trade_id bigint, p_end_trade_id bigint) OWNER TO bindatacoll;

--
-- Name: sp_get_data_quality_stats(text, date, date); Type: FUNCTION; Schema: public; Owner: bindatacoll
--

CREATE FUNCTION public.sp_get_data_quality_stats(p_symbol text, p_start_date date, p_end_date date) RETURNS TABLE("Status" character varying, "BlockCount" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    SELECT
        b."Status",
        COUNT(b."BlockStartDate") AS "BlockCount"
    FROM public."Audit_Blocks" b
    WHERE
        b."Symbol" = p_symbol AND
        b."BlockStartDate" >= p_start_date AND
        b."BlockStartDate" <= p_end_date
    GROUP BY
        b."Status";
END;
$$;


ALTER FUNCTION public.sp_get_data_quality_stats(p_symbol text, p_start_date date, p_end_date date) OWNER TO bindatacoll;

--
-- Name: sp_process_features(); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_process_features() OWNER TO bindatacoll;

--
-- Name: sp_update_tracked_symbols(character varying[]); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_update_tracked_symbols(p_symbols character varying[]) OWNER TO bindatacoll;

--
-- Name: sp_upsert_ohlcv_features(character varying[], bigint[], numeric[], numeric[], numeric[], numeric[], numeric[], numeric[]); Type: FUNCTION; Schema: public; Owner: bindatacoll
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


ALTER FUNCTION public.sp_upsert_ohlcv_features(p_symbols character varying[], p_open_times bigint[], p_rsi_14 numeric[], p_macd_signals numeric[], p_macd_hists numeric[], p_ma_1051200 numeric[], p_ma_201600 numeric[], p_cvds numeric[]) OWNER TO bindatacoll;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: Audit_Blocks; Type: TABLE; Schema: public; Owner: bindatacoll
--

CREATE TABLE public."Audit_Blocks" (
    "Symbol" character varying(20) NOT NULL,
    "BlockStartDate" date NOT NULL,
    "Status" character varying(20) NOT NULL,
    "LastAttempt" timestamp with time zone,
    "RetryCount" integer DEFAULT 0 NOT NULL
);


ALTER TABLE public."Audit_Blocks" OWNER TO bindatacoll;

--
-- Name: HistoricalAudit_Watermarks; Type: TABLE; Schema: public; Owner: bindatacoll
--

CREATE TABLE public."HistoricalAudit_Watermarks" (
    "Symbol" character varying(20) NOT NULL,
    "LastChecked_TradeId" bigint NOT NULL,
    "LastChecked_Timestamp" bigint NOT NULL,
    "Status" character varying(20) NOT NULL,
    "RetryCount" integer DEFAULT 0 NOT NULL,
    "LastAttempt_UTC" timestamp with time zone
);


ALTER TABLE public."HistoricalAudit_Watermarks" OWNER TO bindatacoll;

--
-- Name: Ohlcv_1min; Type: TABLE; Schema: public; Owner: bindatacoll
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


ALTER TABLE public."Ohlcv_1min" OWNER TO bindatacoll;

--
-- Name: Ohlcv_Features; Type: TABLE; Schema: public; Owner: bindatacoll
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


ALTER TABLE public."Ohlcv_Features" OWNER TO bindatacoll;

--
-- Name: Processing_Watermarks; Type: TABLE; Schema: public; Owner: bindatacoll
--

CREATE TABLE public."Processing_Watermarks" (
    "ProcessName" character varying(50) NOT NULL,
    "LastProcessedTimestamp" bigint NOT NULL,
    "Status" character varying(20) NOT NULL,
    "LastUpdate_UTC" timestamp with time zone NOT NULL
);


ALTER TABLE public."Processing_Watermarks" OWNER TO bindatacoll;

--
-- Name: TrackedSymbols; Type: TABLE; Schema: public; Owner: bindatacoll
--

CREATE TABLE public."TrackedSymbols" (
    "Symbol" character varying(20) NOT NULL,
    "IsActive" boolean DEFAULT true NOT NULL,
    "DateAdded" timestamp with time zone DEFAULT now() NOT NULL,
    "LastScanned" timestamp with time zone
);


ALTER TABLE public."TrackedSymbols" OWNER TO bindatacoll;

--
-- Name: Trades; Type: TABLE; Schema: public; Owner: bindatacoll
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
    "IsMyTrade" boolean DEFAULT false,
    "ProcessingStatus" character varying(10) DEFAULT 'new'::character varying NOT NULL
);


ALTER TABLE public."Trades" OWNER TO bindatacoll;

--
-- Name: HistoricalAudit_Watermarks HistoricalAudit_Watermarks_pkey; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."HistoricalAudit_Watermarks"
    ADD CONSTRAINT "HistoricalAudit_Watermarks_pkey" PRIMARY KEY ("Symbol");


--
-- Name: Ohlcv_Features Ohlcv_Features_pkey; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."Ohlcv_Features"
    ADD CONSTRAINT "Ohlcv_Features_pkey" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Audit_Blocks PK_Audit_Blocks; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."Audit_Blocks"
    ADD CONSTRAINT "PK_Audit_Blocks" PRIMARY KEY ("Symbol", "BlockStartDate");


--
-- Name: Ohlcv_1min PK_Ohlcv_1min; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."Ohlcv_1min"
    ADD CONSTRAINT "PK_Ohlcv_1min" PRIMARY KEY ("Symbol", "OpenTime");


--
-- Name: Trades PK_Trades; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."Trades"
    ADD CONSTRAINT "PK_Trades" PRIMARY KEY ("TradeId", "Symbol");


--
-- Name: Processing_Watermarks Processing_Watermarks_pkey; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."Processing_Watermarks"
    ADD CONSTRAINT "Processing_Watermarks_pkey" PRIMARY KEY ("ProcessName");


--
-- Name: TrackedSymbols TrackedSymbols_pkey; Type: CONSTRAINT; Schema: public; Owner: bindatacoll
--

ALTER TABLE ONLY public."TrackedSymbols"
    ADD CONSTRAINT "TrackedSymbols_pkey" PRIMARY KEY ("Symbol");


--
-- Name: IX_Audit_Blocks_Status_LastAttempt; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_Audit_Blocks_Status_LastAttempt" ON public."Audit_Blocks" USING btree ("Status", "LastAttempt");


--
-- Name: IX_HistoricalAudit_Watermarks_Status; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_HistoricalAudit_Watermarks_Status" ON public."HistoricalAudit_Watermarks" USING btree ("Status");


--
-- Name: IX_Ohlcv_1min_ProcessingStatus_OpenTime; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_Ohlcv_1min_ProcessingStatus_OpenTime" ON public."Ohlcv_1min" USING btree ("ProcessingStatus", "OpenTime");


--
-- Name: IX_TrackedSymbols_IsActive; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_TrackedSymbols_IsActive" ON public."TrackedSymbols" USING btree ("IsActive");


--
-- Name: IX_Trades_ProcessingStatus_TradeTime; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_Trades_ProcessingStatus_TradeTime" ON public."Trades" USING btree ("ProcessingStatus", "TradeTime");


--
-- Name: IX_Trades_Symbol_TradeTime; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX "IX_Trades_Symbol_TradeTime" ON public."Trades" USING btree ("Symbol", "TradeTime" DESC);


--
-- Name: idx_trades_symbol_date_utc; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX idx_trades_symbol_date_utc ON public."Trades" USING btree ("Symbol", date((to_timestamp(((("TradeTime")::numeric / 1000.0))::double precision) AT TIME ZONE 'UTC'::text)));


--
-- Name: idx_trades_symbol_tradeid_tradetime; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX idx_trades_symbol_tradeid_tradetime ON public."Trades" USING btree ("Symbol", "TradeId", "TradeTime");


--
-- Name: ix_trades_tradetime_desc; Type: INDEX; Schema: public; Owner: bindatacoll
--

CREATE INDEX ix_trades_tradetime_desc ON public."Trades" USING btree ("TradeTime" DESC);


--
-- PostgreSQL database dump complete
--