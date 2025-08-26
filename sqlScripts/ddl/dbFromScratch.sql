-- ====================================================================
-- Раздел 1: Создание таблиц и индексов
-- ====================================================================

-- Таблица для сырых тиковых данных
CREATE TABLE IF NOT EXISTS public."Trades" (
    "TradeId"         BIGINT          NOT NULL,
    "Symbol"          VARCHAR(20)     NOT NULL,
    "Price"           DECIMAL(18, 8)  NOT NULL,
    "Quantity"        DECIMAL(28, 8)  NOT NULL,
    "QuoteQuantity"   DECIMAL(28, 8)  NOT NULL,
    "TradeTime"       BIGINT          NOT NULL,
    "IsBuyerMaker"    BOOLEAN         NOT NULL,
    "IsBestMatch"     BOOLEAN         NOT NULL,
    "OrderId"         BIGINT,
    "Commission"      DECIMAL(18, 8),
    "CommissionAsset" VARCHAR(10),
    "IsMyTrade"       BOOLEAN         DEFAULT FALSE,
    CONSTRAINT "PK_Trades" PRIMARY KEY ("TradeId", "Symbol")
);

-- Ключевой индекс для ускорения агрегации и выборок по времени
CREATE INDEX IF NOT EXISTS "IX_Trades_Symbol_TradeTime" ON public."Trades" ("Symbol", "TradeTime" DESC);


-- Управляющая таблица для отслеживаемых символов
CREATE TABLE IF NOT EXISTS public."TrackedSymbols" (
    "Symbol"      VARCHAR(20) PRIMARY KEY,
    "IsActive"    BOOLEAN     NOT NULL DEFAULT TRUE,
    "DateAdded"   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "LastScanned" TIMESTAMPTZ NULL
);

-- Индекс для быстрой выборки активных символов
CREATE INDEX IF NOT EXISTS "IX_TrackedSymbols_IsActive" ON public."TrackedSymbols" ("IsActive");


-- Таблица для агрегированных 1-минутных свечей
CREATE TABLE IF NOT EXISTS public."Ohlcv_1min" (
    "Symbol"     VARCHAR(20)    NOT NULL,
    "OpenTime"   BIGINT         NOT NULL,
    "OpenPrice"  DECIMAL(18, 8) NOT NULL,
    "HighPrice"  DECIMAL(18, 8) NOT NULL,
    "LowPrice"   DECIMAL(18, 8) NOT NULL,
    "ClosePrice" DECIMAL(18, 8) NOT NULL,
    "Volume"     DECIMAL(28, 8) NOT NULL,
    CONSTRAINT "PK_Ohlcv_1min" PRIMARY KEY ("Symbol", "OpenTime")
);

-- Таблица для рассчитанных индикаторов (признаков)
CREATE TABLE IF NOT EXISTS public."Ohlcv_Features" (
    "Symbol"         VARCHAR(20)    NOT NULL,
    "OpenTime"       BIGINT         NOT NULL,
    "RSI_14"         DECIMAL(10, 4),
    "MACD_Signal"    DECIMAL(18, 8),
    "MACD_Hist"      DECIMAL(18, 8),
    "MA_1051200"     DECIMAL(18, 8),
    "MA_201600"      DECIMAL(18, 8),
    "CVD"            DECIMAL(28, 8),
    CONSTRAINT "PK_Ohlcv_Features" PRIMARY KEY ("Symbol", "OpenTime")
);


-- ====================================================================
-- Раздел 2: Создание функций (аналог хранимых процедур)
-- ====================================================================

-- Функция для массовой вставки сделок
CREATE OR REPLACE FUNCTION public.sp_bulk_insert_trades(
    p_trade_ids BIGINT[], p_symbols VARCHAR(20)[], p_prices DECIMAL(18,8)[],
    p_quantities DECIMAL(28,8)[], p_quote_quantities DECIMAL(28,8)[],
    p_trade_times BIGINT[], p_is_buyer_makers BOOLEAN[], p_is_best_matches BOOLEAN[]
) RETURNS VOID AS $$
BEGIN
    INSERT INTO public."Trades" ("TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity", "TradeTime", "IsBuyerMaker", "IsBestMatch")
    SELECT * FROM UNNEST(p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities, p_trade_times, p_is_buyer_makers, p_is_best_matches)
    ON CONFLICT ("TradeId", "Symbol") DO NOTHING;
END;
$$ LANGUAGE plpgsql;


-- Функция для "умного" обновления списка отслеживаемых символов
CREATE OR REPLACE FUNCTION public.sp_update_tracked_symbols(p_symbols VARCHAR(20)[]) RETURNS VOID AS $$
BEGIN
    UPDATE public."TrackedSymbols" SET "IsActive" = FALSE WHERE "IsActive" = TRUE AND "Symbol" <> ALL(p_symbols);
    INSERT INTO public."TrackedSymbols" ("Symbol", "IsActive", "LastScanned")
    SELECT symbol, TRUE, NOW() FROM UNNEST(p_symbols) AS u(symbol)
    ON CONFLICT ("Symbol") DO UPDATE SET "IsActive" = TRUE, "LastScanned" = NOW();
END;
$$ LANGUAGE plpgsql;


-- Функция для агрегации тиковых данных в 1-минутные свечи
CREATE OR REPLACE FUNCTION public.sp_aggregate_trades_to_ohlcv() RETURNS VOID AS $$
DECLARE
    last_processed_time BIGINT;
    interval BIGINT := 60000;
BEGIN
    SELECT COALESCE(MAX("OpenTime"), 0) INTO last_processed_time FROM public."Ohlcv_1min";
    CREATE TEMP TABLE NewAggregates ON COMMIT DROP AS
    SELECT "Symbol", ("TradeTime" / interval) * interval AS "OpenTime", MIN("Price") AS "LowPrice", MAX("Price") AS "HighPrice",
           SUM("Quantity") AS "Volume", MIN("TradeId") AS "FirstTradeId", MAX("TradeId") AS "LastTradeId"
    FROM public."Trades" WHERE "TradeTime" >= last_processed_time GROUP BY "Symbol", ("TradeTime" / interval);
    IF NOT FOUND THEN RETURN; END IF;
    WITH FinalCandles AS (
        SELECT agg."Symbol", agg."OpenTime", first_trade."Price" AS "OpenPrice", agg."HighPrice", agg."LowPrice", last_trade."Price" AS "ClosePrice", agg."Volume"
        FROM NewAggregates AS agg
        JOIN public."Trades" AS first_trade ON agg."FirstTradeId" = first_trade."TradeId"
        JOIN public."Trades" AS last_trade ON agg."LastTradeId" = last_trade."TradeId"
    )
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume")
    SELECT "Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume" FROM FinalCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = public."Ohlcv_1min"."Volume" + EXCLUDED."Volume";
END;
$$ LANGUAGE plpgsql;


-- Функция для массовой вставки/обновления рассчитанных индикаторов
CREATE OR REPLACE FUNCTION public.sp_upsert_ohlcv_features(
    p_symbols         VARCHAR(20)[], p_open_times      BIGINT[],
    p_rsi_14          NUMERIC[], p_macd_signals    NUMERIC[], p_macd_hists      NUMERIC[],
    p_ma_1051200      NUMERIC[], p_ma_201600       NUMERIC[], p_cvds            NUMERIC[]
) RETURNS VOID AS $$
BEGIN
    CREATE TEMP TABLE NewFeatures ON COMMIT DROP AS
    SELECT * FROM UNNEST(
        p_symbols, p_open_times, p_rsi_14, p_macd_signals, p_macd_hists,
        p_ma_1051200, p_ma_201600, p_cvds
    ) AS t("Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "MA_1051200", "MA_201600", "CVD");
    INSERT INTO public."Ohlcv_Features" ("Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "MA_1051200", "MA_201600", "CVD")
    SELECT * FROM NewFeatures
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET "RSI_14" = EXCLUDED."RSI_14", "MACD_Signal" = EXCLUDED."MACD_Signal", "MACD_Hist" = EXCLUDED."MACD_Hist",
        "MA_1051200" = EXCLUDED."MA_1051200", "MA_201600" = EXCLUDED."MA_201600", "CVD" = EXCLUDED."CVD";
END;
$$ LANGUAGE plpgsql;

-- Возвращает таблицу с найденными дырами (начало и конец)
CREATE OR REPLACE FUNCTION public.sp_find_trade_gaps(
    p_symbol VARCHAR(20),
    p_min_gap_seconds INT -- Минимальный размер дыры в секундах, который считать проблемой
)
RETURNS TABLE("GapStart" BIGINT, "GapEnd" BIGINT) AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT
            "TradeTime",
            -- Получаем время ПРЕДЫДУЩЕЙ сделки
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
    )
    SELECT
        "PrevTradeTime" AS "GapStart",
        "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE
        -- Находим разрыв, который больше нашего порога
        ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000)
    -- Добавляем проверку на самую свежую дыру (между концом и "сейчас")
    UNION ALL
    SELECT
        MAX("TradeTime"),
        (EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT
    FROM public."Trades"
    WHERE "Symbol" = p_symbol
    HAVING ((EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT - MAX("TradeTime")) > (p_min_gap_seconds * 1000);
END;
$$ LANGUAGE plpgsql;