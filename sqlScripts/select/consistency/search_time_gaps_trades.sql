-- Найти все РЕАЛЬНЫЕ пропуски в последовательности TradeId для BTCUSDT

WITH OrderedTrades AS (
    SELECT
        "TradeId",
        "TradeTime", 
        -- Получаем ID ПРЕДЫДУЩЕЙ сделки
        LAG("TradeId", 1) OVER (PARTITION BY "Symbol" ORDER BY "TradeId" ASC) AS "PrevTradeId",
        -- Получаем время ПРЕДЫДУЩЕЙ сделки, чтобы видеть временные рамки дыры
        LAG("TradeTime", 1) OVER (PARTITION BY "Symbol" ORDER BY "TradeId" ASC) AS "PrevTradeTime"
    FROM public."Trades"
    WHERE "Symbol" = 'BTCUSDT' -- Укажите нужный символ
)
SELECT
    "PrevTradeId" + 1 AS "FirstMissing_TradeId",
    "TradeId" - 1 AS "LastMissing_TradeId",
    ("TradeId" - "PrevTradeId" - 1) AS "MissingTrades_Count",
    to_timestamp("PrevTradeTime" / 1000.0) AS "GapStart_UTC",
    to_timestamp("TradeTime" / 1000.0) AS "GapEnd_UTC"
FROM OrderedTrades
WHERE
    -- Дыра есть, если текущий ID не является следующим за предыдущим
    "TradeId" > "PrevTradeId" + 1
ORDER BY 
    "MissingTrades_Count" DESC;