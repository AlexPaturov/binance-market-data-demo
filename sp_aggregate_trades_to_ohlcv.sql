CREATE OR REPLACE FUNCTION public.sp_aggregate_trades_to_ohlcv(
    p_start_timestamp bigint,
    p_end_timestamp bigint
)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    -- Интервал в 1 минуту (в миллисекундах)
    interval_ms BIGINT := 60000; 
BEGIN
    -- Мы больше не ищем временные рамки внутри. Мы получаем их как параметры.
    
    -- 1. Агрегируем тики ТОЛЬКО в заданном "окне" во временную таблицу.
    -- Это позволяет избежать многократного сканирования основной таблицы.
    CREATE TEMP TABLE NewCandles ON COMMIT DROP AS
    WITH TradesInWindow AS (
        -- Сначала отбираем все сделки в нашем окне, чтобы последующие CTE
        -- работали с уже отфильтрованным, меньшим набором данных.
        SELECT "Symbol", "TradeId", "Price", "Quantity", "TradeTime"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= p_start_timestamp
          AND "TradeTime" < p_end_timestamp -- Используем '<', чтобы не захватить начало следующего окна
    ),
    Aggregates AS (
        -- Считаем основные агрегаты: Min/Max Price, Volume и находим ID первой/последней сделки
        SELECT
            "Symbol",
            ("TradeTime" / interval_ms) * interval_ms AS "OpenTime",
            MIN("Price") AS "LowPrice",
            MAX("Price") AS "HighPrice",
            SUM("Quantity") AS "Volume",
            -- Используем MIN/MAX по TradeId, так как он гарантированно уникален и отсортирован по времени
            MIN("TradeId") AS "FirstTradeId",
            MAX("TradeId") AS "LastTradeId"
        FROM TradesInWindow
        GROUP BY "Symbol", "OpenTime"
    ),
    OpenClosePrices AS (
        -- Находим цены открытия и закрытия одним проходом по таблице Trades,
        -- используя ID, которые мы нашли на предыдущем шаге.
        SELECT
            "TradeId",
            "Price"
        FROM public."Trades"
        WHERE "TradeId" IN (SELECT "FirstTradeId" FROM Aggregates UNION SELECT "LastTradeId" FROM Aggregates)
    )
    -- Собираем финальную свечу
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

    -- Если во временной таблице ничего нет, значит, в этом окне не было новых сделок.
    -- Выходим, чтобы не выполнять лишние операции.
    IF NOT EXISTS (SELECT 1 FROM NewCandles) THEN
        RETURN;
    END IF;

    -- 2. Вставляем/обновляем свечи, используя данные из временной таблицы.
    -- ON CONFLICT - это "умный" UPSERT (UPDATE or INSERT).
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        "Symbol",
        "OpenTime",
        "OpenPrice",
        "HighPrice",
        "LowPrice",
        "ClosePrice",
        "Volume",
        'new' -- Новые свечи готовы для расчета индикаторов
    FROM NewCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        -- Если свеча за эту минуту уже существует (например, мы обрабатываем сделки
        -- из середины минуты), мы не перезаписываем ее, а корректно обновляем:
        "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice", -- Цена закрытия всегда берется из новой пачки
        "Volume" = public."Ohlcv_1min"."Volume" + EXCLUDED."Volume", -- Объемы суммируются
        "ProcessingStatus" = 'new'; -- Сбрасываем статус, так как свеча обновилась

    -- 3. Помечаем обработанные тики как "processed" ТОЛЬКО в этом окне.
    -- Это предотвращает их повторную обработку.
    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE 
        "ProcessingStatus" = 'new'
        AND "TradeTime" >= p_start_timestamp
        AND "TradeTime" < p_end_timestamp;

END;
$function$;