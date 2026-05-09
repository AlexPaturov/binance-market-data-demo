CREATE TABLE IF NOT EXISTS public."Ohlcv_Features" (
"Symbol" VARCHAR(20) NOT NULL,
"OpenTime" BIGINT NOT NULL,

-- Индикаторы на основе цены (Price-based)
"RSI_14" DECIMAL(10, 4), -- Relative Strength Index (14 периодов)
"MACD_Signal" DECIMAL(18, 8), -- MACD (12,26,9) - линия сигнала
"MACD_Hist" DECIMAL(18, 8), -- MACD (12,26,9) - гистограмма

-- Скользящие средние (Moving Averages)
"MA_1051200" DECIMAL(18, 8), -- 2-year MA (2*365*24*60 = 1,051,200 минут)
"MA_201600" DECIMAL(18, 8), -- 200-week MA (200*7*24*60 = 2,016,000 минут)

-- Индикаторы на основе объема (Volume-based)
"CVD" DECIMAL(28, 8), -- Cumulative Volume Delta

PRIMARY KEY ("Symbol", "OpenTime")
);