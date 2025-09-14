SELECT
    to_timestamp("TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapEnd_UTC"
FROM
    public."Trades"
WHERE
    "Symbol" = :symbol AND
    "TradeTime" BETWEEN (EXTRACT(EPOCH FROM '2025-09-11 01:04:00'::TIMESTAMPTZ) * 1000)::BIGINT 
                  AND (EXTRACT(EPOCH FROM '2025-09-12 06:05:00'::TIMESTAMPTZ) * 1000)::BIGINT
order by "TradeTime" desc
LIMIT 10;