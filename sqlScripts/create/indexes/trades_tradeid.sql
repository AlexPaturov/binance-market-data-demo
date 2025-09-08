-- 4. ИНДЕКС только на TradeId для быстрой сортировки (если нужен)
-- Может быть полезен для LAG() операций
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trades_tradeid 
ON public."Trades" ("TradeId");