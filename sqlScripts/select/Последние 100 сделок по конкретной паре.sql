SELECT
    "TradeId",
    "Symbol",
    to_timestamp("TradeTime" / 1000.0) AS "TradeDateTime", -- Преобразование Unix ms в дату
    "Price",
    "Quantity"
FROM
    public."Trades"
WHERE
    "Symbol" = :symbol -- DBeaver спросит значение, например 'BTCUSDT'
ORDER BY
    "TradeTime" DESC
LIMIT 1000;