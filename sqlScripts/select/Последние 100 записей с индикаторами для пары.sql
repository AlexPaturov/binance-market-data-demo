SELECT
    to_timestamp("OpenTime" / 1000.0) AS "OpenDateTime",
    "Symbol",
    "RSI_14",
    "MACD_Signal",
    "MACD_Hist",
    "CVD"
FROM
    public."Ohlcv_Features"
WHERE
    "Symbol" = :symbol -- Например, 'SOLUSDT'
ORDER BY
    "OpenTime" DESC
LIMIT 100;