-- Функция для поиска ВСЕХ разрывов в тиковых данных для указанного символа
CREATE OR REPLACE FUNCTION public.sp_find_trade_gaps(
    p_symbol TEXT,
    p_min_gap_seconds INTEGER
)
RETURNS TABLE("GapStart" BIGINT, "GapEnd" BIGINT) AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
    )
    SELECT
        "PrevTradeTime" AS "GapStart",
        "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000)

    UNION ALL

    SELECT
        MAX(t."TradeTime") AS "GapStart",
        (EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT AS "GapEnd"
    FROM public."Trades" t
    WHERE t."Symbol" = p_symbol
    HAVING ((EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT - MAX(t."TradeTime")) > (p_min_gap_seconds * 1000);
END;
$$ LANGUAGE plpgsql;
