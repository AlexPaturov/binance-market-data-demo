-- Функция для поиска разрывов в данных ТОЛЬКО в указанном временном окне.
CREATE OR REPLACE FUNCTION public.sp_find_gaps_in_window(
    p_symbol TEXT,
    p_start_time_ms BIGINT,
    p_end_time_ms BIGINT,
    p_min_gap_seconds INTEGER
)
RETURNS TABLE("GapStart" BIGINT, "GapEnd" BIGINT) AS $$
BEGIN
    RETURN QUERY
    WITH WindowTrades AS (
        -- Выбираем сделки только в нашем "окне" + одну сделку ДО него,
        -- чтобы правильно определить первую дыру в блоке.
        SELECT * FROM (
            SELECT "TradeId", "TradeTime"
            FROM public."Trades"
            WHERE "Symbol" = p_symbol AND "TradeTime" < p_start_time_ms
            ORDER BY "TradeTime" DESC
            LIMIT 1
        ) AS before_window
        UNION ALL
        SELECT "TradeId", "TradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
          AND "TradeTime" >= p_start_time_ms
          AND "TradeTime" <= p_end_time_ms
    ),
    OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM WindowTrades
    )
    SELECT
        "PrevTradeTime" AS "GapStart",
        "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE
        "PrevTradeTime" IS NOT NULL AND -- Игнорируем самую первую запись
        ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000);
END;
$$ LANGUAGE plpgsql;