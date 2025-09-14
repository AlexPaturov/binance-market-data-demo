--SELECT
--    to_timestamp("TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapEnd_UTC"
--FROM
--    public."Trades"
--WHERE
--    --"Symbol" = :symbol and 
--    "Symbol" = 'ETHUSDT' and 
--    "TradeTime" BETWEEN (EXTRACT(EPOCH FROM '2025-09-10 01:04:00'::TIMESTAMPTZ) * 1000)::BIGINT 
--                  AND (EXTRACT(EPOCH FROM '2025-09-13 16:05:00'::TIMESTAMPTZ) * 1000)::BIGINT
--order by "TradeTime" DESC
--LIMIT 10;

SELECT
	tr."Symbol", 	
	tr."TradeId",
	tr."ProcessingStatus",
	to_timestamp(tr."TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "GapEnd_UTC"
FROM
    public."Trades" tr
WHERE
    --"Symbol" = :symbol and 
    -- "Symbol" = 'FDUSDUSDT' and 
    "Symbol" = 'ETHUSDT' and 
    "TradeId" BETWEEN 2845869666
                  AND 2845888377
order by "TradeId"
LIMIT 1000;

