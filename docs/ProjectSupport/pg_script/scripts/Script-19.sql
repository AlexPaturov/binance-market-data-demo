SELECT
    to_timestamp("OpenTime" / 1000.0) AS "OpenDateTime",
    "Symbol",
    "OpenPrice",
    "HighPrice",
    "LowPrice",
    "ClosePrice",
    "Volume"
FROM
    public."Ohlcv_1min"
WHERE
    "Symbol" = :symbol -- Например, 'ETHUSDT'
ORDER BY
    "OpenTime" DESC
LIMIT 100;