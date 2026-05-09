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
$function$;