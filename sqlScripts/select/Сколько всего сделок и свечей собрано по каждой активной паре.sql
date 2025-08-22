SELECT
    s."Symbol",
    s."IsActive",
    COUNT(t."TradeId") AS "TotalTrades",
    COUNT(o."OpenTime") AS "TotalOhlcv_1min"
FROM
    public."TrackedSymbols" s
LEFT JOIN
    public."Trades" t ON s."Symbol" = t."Symbol"
LEFT JOIN
    public."Ohlcv_1min" o ON s."Symbol" = o."Symbol"
WHERE
    s."IsActive" = true
GROUP BY
    s."Symbol", s."IsActive"
ORDER BY
    "TotalTrades" DESC;