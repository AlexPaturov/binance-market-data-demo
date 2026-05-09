CREATE TABLE IF NOT EXISTS public."Ohlcv_Features" (
    "Symbol"         VARCHAR(20)    NOT NULL,
    "OpenTime"       BIGINT         NOT NULL,

    -- Индикаторы на основе цены (Price-based)
    "RSI_14"         DECIMAL(10, 4),
    "MACD_Signal"    DECIMAL(18, 8),
    "MACD_Hist"      DECIMAL(18, 8),

    -- Скользящие средние (Moving Averages)
    "MA_1051200"     DECIMAL(18, 8),
    "MA_201600"      DECIMAL(18, 8),

    -- Индикаторы на основе объема (Volume-based)
    "CVD"            DECIMAL(28, 8),   -- <--- ВОТ ИСПРАВЛЕННАЯ СТРОКА С ЗАПЯТОЙ

    CONSTRAINT "PK_Ohlcv_Features" PRIMARY KEY ("Symbol", "OpenTime")
);