CREATE OR REPLACE FUNCTION public.sp_aggregate_trades_to_ohlcv()
RETURNS VOID AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
    interval BIGINT := 60000;
BEGIN
    -- 1. Получаем "вотермарку"
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'OhlcvAggregator';

    -- 2. Находим конец "окна"
    SELECT MAX("TradeTime") INTO end_timestamp
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    -- 3. Агрегируем данные, используя оконные функции для исключения дубликатов
    WITH RelevantTrades AS (
        SELECT * FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= start_timestamp
          AND "TradeTime" <= end_timestamp
    ),
    CandleData AS (
        SELECT
            "Symbol",
            ("TradeTime" / interval) * interval AS "OpenTime",
            first_value("Price") OVER (PARTITION BY "Symbol", ("TradeTime" / interval) ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "OpenPrice",
            last_value("Price") OVER (PARTITION BY "Symbol", ("TradeTime" / interval) ORDER BY "TradeTime" ASC, "TradeId" ASC ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS "ClosePrice",
            "Price",
            "Quantity"
        FROM RelevantTrades
    ),
    FinalCandles AS (
        -- Теперь группируем, чтобы получить ОДНУ строку на свечу
        SELECT
            "Symbol",
            "OpenTime",
            MIN("OpenPrice") AS "OpenPrice",
            MAX("Price") AS "HighPrice",
            MIN("Price") AS "LowPrice",
            MIN("ClosePrice") AS "ClosePrice",
            SUM("Quantity") AS "Volume"
        FROM CandleData
        GROUP BY "Symbol", "OpenTime"
    )
    -- 4. Вставляем/обновляем свечи
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume")
    SELECT "Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume" FROM FinalCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = EXCLUDED."Volume"; -- Обновляем на полный пересчитанный объем

    -- 5. Помечаем обработанные тики как "processed"
    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new'
      AND "TradeTime" >= start_timestamp
      AND "TradeTime" <= end_timestamp;

    -- 6. Сдвигаем "вотермарку"
    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'OhlcvAggregator';
END;
$$ LANGUAGE plpgsql;