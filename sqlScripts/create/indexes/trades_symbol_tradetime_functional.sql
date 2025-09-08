SELECT tr."TradeId",  to_timestamp(tr."TradeTime" / 1000.0) AS "TradeTime_UTC" , tr."ProcessingStatus", tr."Symbol" 
FROM public."Trades" tr
WHERE "Symbol" = 'BTCUSDT'
  AND "TradeTime" BETWEEN 
      EXTRACT(EPOCH FROM '2025-09-06 13:34:58'::timestamp AT TIME ZONE 'UTC')::bigint * 1000 AND
      EXTRACT(EPOCH FROM '2025-09-06 13:38:00'::timestamp AT TIME ZONE 'UTC')::bigint * 1000
order by "TradeId";
