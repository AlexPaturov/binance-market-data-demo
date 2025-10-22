CREATE OR REPLACE FUNCTION public.sp_aggregate_trades_to_ohlcv(
    p_start_timestamp bigint,
    p_end_timestamp bigint
)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    interval_ms BIGINT := 60000; 
BEGIN
    CREATE TEMP TABLE NewCandles ON COMMIT DROP AS
    WITH TradesInWindow AS (
        SELECT "Symbol", "TradeId", "Price", "Quantity", "TradeTime"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= p_start_timestamp
          AND "TradeTime" < p_end_timestamp 
    ),
    Aggregates AS (
        SELECT
            "Symbol",
            ("TradeTime" / interval_ms) * interval_ms AS "OpenTime",
            MIN("Price") AS "LowPrice",
            MAX("Price") AS "HighPrice",
            SUM("Quantity") AS "Volume",
            MIN("TradeId") AS "FirstTradeId",
            MAX("TradeId") AS "LastTradeId"
        FROM TradesInWindow
        GROUP BY "Symbol", "OpenTime"
    ),
    OpenClosePrices AS (
        SELECT
            "TradeId",
            "Price"
        FROM public."Trades"
        WHERE "TradeId" IN (SELECT "FirstTradeId" FROM Aggregates UNION SELECT "LastTradeId" FROM Aggregates)
    )
    SELECT
        agg."Symbol",
        agg."OpenTime",
        first_trade."Price" AS "OpenPrice",
        agg."HighPrice",
        agg."LowPrice",
        last_trade."Price" AS "ClosePrice",
        agg."Volume"
    FROM Aggregates agg
    JOIN OpenClosePrices first_trade ON agg."FirstTradeId" = first_trade."TradeId"
    JOIN OpenClosePrices last_trade ON agg."LastTradeId" = last_trade."TradeId";

    IF NOT EXISTS (SELECT 1 FROM NewCandles) THEN
        RETURN;
    END IF;

    -- ON CONFLICT - ??? "?????" UPSERT (UPDATE or INSERT).
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        "Symbol",
        "OpenTime",
        "OpenPrice",
        "HighPrice",
        "LowPrice",
        "ClosePrice",
        "Volume",
        'new'
    FROM NewCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = public."Ohlcv_1min"."Volume" + EXCLUDED."Volume", 
        "ProcessingStatus" = 'new';

    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE 
        "ProcessingStatus" = 'new'
        AND "TradeTime" >= p_start_timestamp
        AND "TradeTime" < p_end_timestamp;

END;
$function$;