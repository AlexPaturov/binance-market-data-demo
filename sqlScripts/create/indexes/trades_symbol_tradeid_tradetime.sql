-- 1. ОСНОВНОЙ СОСТАВНОЙ ИНДЕКС для фильтрации и сортировки
-- Покрывает: WHERE Symbol + ORDER BY TradeId + SELECT TradeId, TradeTime
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trades_symbol_tradeid_tradetime 
ON public."Trades" ("Symbol", "TradeId", "TradeTime");