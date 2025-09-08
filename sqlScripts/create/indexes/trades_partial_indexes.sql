-- 5. ЧАСТИЧНЫЙ ИНДЕКС для часто запрашиваемых символов (опционально)
-- Создайте для самых активных символов, например:
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trades_btcusdt_tradeid_time 
ON public."Trades" ("TradeId", "TradeTime") 
WHERE "Symbol" = 'BTCUSDT';

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trades_ethusdt_tradeid_time 
ON public."Trades" ("TradeId", "TradeTime") 
WHERE "Symbol" = 'ETHUSDT';