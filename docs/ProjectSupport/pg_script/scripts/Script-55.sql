EXPLAIN ANALYZE
WITH DateSeries AS (
 SELECT generate_series(
        (SELECT date_trunc('day', MIN("DateAdded")) FROM public."TrackedSymbols"),
        date_trunc('day', NOW() AT TIME ZONE 'utc'),
        '3 day'::interval
    )::date AS BlockDate
),
Symbols AS (
    SELECT "Symbol" FROM public."TrackedSymbols"
)
SELECT
    s."Symbol",
    ds.BlockDate AS "BlockStartDate",
    'Pending' AS "Status"
FROM Symbols s
CROSS JOIN DateSeries ds
LEFT JOIN public."Audit_Blocks" existing_blocks 
    ON s."Symbol" = existing_blocks."Symbol" AND ds.BlockDate = existing_blocks."BlockStartDate"
WHERE existing_blocks."Symbol" IS NULL;