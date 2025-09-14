SELECT 
    tr."TradeId", 
    to_timestamp(tr."TradeTime" / 1000.0) AT TIME ZONE 'UTC' AS "TradeTime_UTC",
    tr."ProcessingStatus", 
    tr."Symbol"
FROM public."Trades" tr
WHERE "Symbol" = 'BTCUSDT'
  AND "TradeTime" BETWEEN
      EXTRACT(EPOCH FROM '2025-09-10 13:34:58'::timestamp AT TIME ZONE 'UTC')::bigint * 1000 AND
      EXTRACT(EPOCH FROM '2025-09-11 23:38:00'::timestamp AT TIME ZONE 'UTC')::bigint * 1000
ORDER BY "TradeTime" desc
limit 10;

-- Тот же запрос - считаю полученные строки
--SELECT 
--    count(*)
--FROM public."Trades" tr
--WHERE "Symbol" = 'BTCUSDT'
--  AND "TradeTime" between
--      EXTRACT(EPOCH FROM '2025-09-06 00:00:00'::timestamp AT TIME ZONE 'UTC')::bigint * 1000 AND
--      EXTRACT(EPOCH FROM '2025-09-06 23:59:59'::timestamp AT TIME ZONE 'UTC')::bigint * 1000;