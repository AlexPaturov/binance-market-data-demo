CREATE OR REPLACE FUNCTION public.sp_aggregate_trades_to_ohlcv()
RETURNS VOID AS $$
DECLARE
    -- Переменные для хранения "окна" обработки
    start_timestamp BIGINT;
    end_timestamp BIGINT;
    last_processed_trade_time BIGINT;
BEGIN
    -- 1. Получаем "вотермарку" - нашу точку старта
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'OhlcvAggregator';

    -- 2. Находим время последней доступной сделки, чтобы определить конец "окна"
    SELECT MAX("TradeTime") INTO end_timestamp
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp;

    -- Если новых сделок нет, просто выходим
    IF end_timestamp IS NULL THEN
        RETURN;
    END IF;

    -- 3. Агрегируем только "новые" тики в найденном "окне"
    CREATE TEMP TABLE NewCandles ON COMMIT DROP AS
    WITH RelevantTrades AS (
        SELECT * FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= start_timestamp
          AND "TradeTime" <= end_timestamp
    ),
    Aggregates AS (
        SELECT "Symbol", ("TradeTime" / 60000) * 60000 AS "OpenTime", MIN("Price") AS "LowPrice", MAX("Price") AS "HighPrice",
               SUM("Quantity") AS "Volume", MIN("TradeId") AS "FirstTradeId", MAX("TradeId") AS "LastTradeId"
        FROM RelevantTrades GROUP BY 1, 2
    )
    SELECT agg."Symbol", agg."OpenTime", f."Price" AS "OpenPrice", agg."HighPrice", agg."LowPrice", l."Price" AS "ClosePrice", agg."Volume"
    FROM Aggregates agg
    JOIN public."Trades" f ON agg."FirstTradeId" = f."TradeId"
    JOIN public."Trades" l ON agg."LastTradeId" = l."TradeId";

    -- Если ничего не сагрегировалось, выходим
    IF NOT FOUND THEN RETURN; END IF;

    -- 4. Вставляем/обновляем свечи в основной таблице
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume")
    SELECT "Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume" FROM NewCandles
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

    -- 6. Сдвигаем "вотермарку" вперед
    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'OhlcvAggregator';

END;
$$ LANGUAGE plpgsql;

SELECT 'Функция sp_aggregate_trades_to_ohlcv успешно обновлена до инкрементальной версии.' AS "Статус";