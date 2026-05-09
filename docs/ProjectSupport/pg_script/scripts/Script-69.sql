SELECT
    to_timestamp("TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapEnd_UTC"
FROM
    public."Trades"
WHERE
    --"Symbol" = :symbol and 
    "Symbol" = 'ETHUSDT' and 
    "TradeTime" BETWEEN (EXTRACT(EPOCH FROM '2025-09-10 01:04:00'::TIMESTAMPTZ) * 1000)::BIGINT 
                  AND (EXTRACT(EPOCH FROM '2025-09-13 16:05:00'::TIMESTAMPTZ) * 1000)::BIGINT
order by "TradeTime" DESC
LIMIT 10;