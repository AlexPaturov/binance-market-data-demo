SELECT
    to_timestamp("TradeTime" / 1000.0) AS "TradeDateTime",
    "Price",
    "Quantity",
    "QuoteQuantity",
    "IsBuyerMaker"
FROM
    public."Trades"
WHERE
    "Symbol" = :symbol AND
    "TradeTime" BETWEEN (EXTRACT(EPOCH FROM '2025-08-20 15:00:00'::TIMESTAMPTZ) * 1000)::BIGINT 
                  AND (EXTRACT(EPOCH FROM '2025-08-20 15:59:59'::TIMESTAMPTZ) * 1000)::BIGINT
ORDER BY
    "TradeTime" ASC;