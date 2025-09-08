-- 2. ИНДЕКС для фильтрации по времени (конвертация TradeTime в дату)
-- Ускоряет: DATE(to_timestamp("TradeTime" / 1000.0))
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trades_symbol_tradetime 
ON public."Trades" ("Symbol", "TradeTime");