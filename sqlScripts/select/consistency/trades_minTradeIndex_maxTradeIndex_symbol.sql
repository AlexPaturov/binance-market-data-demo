WITH DateParams AS (
    -- Задайте здесь параметры поиска
    SELECT 
        'BTCUSDT' AS symbol,
        '2025-09-06'::date AS start_date,
        '2025-09-07'::date AS end_date
),
FilteredTrades AS (
    SELECT 
        t."TradeId",
        t."TradeTime",
        t."Symbol"
    FROM public."Trades" t
    CROSS JOIN DateParams dp
    WHERE t."Symbol" = dp.symbol
      AND DATE(to_timestamp(t."TradeTime" / 1000.0) AT TIME ZONE 'UTC') >= dp.start_date
      AND DATE(to_timestamp(t."TradeTime" / 1000.0) AT TIME ZONE 'UTC') <= dp.end_date
),
OrderedTrades AS (
    SELECT
        "TradeId",
        "TradeTime", 
        -- Получаем ID ПРЕДЫДУЩЕЙ сделки
        LAG("TradeId", 1) OVER (PARTITION BY "Symbol" ORDER BY "TradeId" ASC) AS "PrevTradeId",
        -- Получаем время ПРЕДЫДУЩЕЙ сделки, чтобы видеть временные рамки дыры
        LAG("TradeTime", 1) OVER (PARTITION BY "Symbol" ORDER BY "TradeId" ASC) AS "PrevTradeTime"
    FROM FilteredTrades
)
SELECT
    "PrevTradeId" + 1 AS "FirstMissing_TradeId",
    "TradeId" - 1 AS "LastMissing_TradeId",
    ("TradeId" - "PrevTradeId" - 1) AS "MissingTrades_Count",
    to_timestamp("PrevTradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapStart_UTC",
    to_timestamp("TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapEnd_UTC",
    -- Дополнительно: длительность пропуска
    EXTRACT(EPOCH FROM (
        to_timestamp("TradeTime" / 1000.0) - to_timestamp("PrevTradeTime" / 1000.0)
    )) / 60 AS "Gap_Duration_Minutes"
FROM OrderedTrades
WHERE
    -- Дыра есть, если текущий ID не является следующим за предыдущим
    "TradeId" > "PrevTradeId" + 1
    AND "PrevTradeId" IS NOT NULL  -- Исключаем первую запись
ORDER BY 
    "MissingTrades_Count" DESC;

--SELECT current_setting('timezone');