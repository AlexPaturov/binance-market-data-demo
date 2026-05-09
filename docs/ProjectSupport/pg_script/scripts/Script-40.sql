SELECT
    count(*)
FROM
    public."Trades"
WHERE
    "Symbol" = :symbol AND
    "TradeTime" BETWEEN (EXTRACT(EPOCH FROM '2025-08-30 14:04:00'::TIMESTAMPTZ) * 1000)::BIGINT 
                  AND (EXTRACT(EPOCH FROM '2025-08-30 14:05:00'::TIMESTAMPTZ) * 1000)::BIGINT;