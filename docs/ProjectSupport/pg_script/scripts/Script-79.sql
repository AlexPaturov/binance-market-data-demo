SELECT * FROM "Trades" ORDER BY "TradeTime" DESC LIMIT 1;

CREATE INDEX IX_Trades_TradeTime_Desc ON public."Trades" ("TradeTime" DESC);