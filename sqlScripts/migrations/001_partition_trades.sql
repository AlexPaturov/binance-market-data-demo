-- Migration 001: Partition Trades table by month (RANGE on TradeTime)
-- Rolling window: 13 months. 14th oldest month is dropped automatically by sp_rotate_trades_partition.
--
-- Run on a FRESH database only (Trades must be empty).
-- Apply AFTER prod_schema_2026-05-09.sql.
--
-- Changes vs previous schema:
--   - Trades: PK (TradeId, Symbol) → (TradeId, Symbol, TradeTime)  [required by PG for partitioning]
--   - Trades: PARTITION BY RANGE ("TradeTime")
--   - sp_bulk_insert_trades: ON CONFLICT updated for new PK
--   - New: sp_ensure_trades_partition(BIGINT) — creates partition for target month
--   - New: sp_rotate_trades_partition()       — creates current+next month, drops 14th oldest

-- ============================================================
-- 1. Drop old Trades table (indexes cascade automatically)
-- ============================================================
DROP TABLE IF EXISTS public."Trades" CASCADE;

-- ============================================================
-- 2. Create partitioned Trades table
-- ============================================================
CREATE TABLE public."Trades" (
    "TradeId"          BIGINT          NOT NULL,
    "Symbol"           VARCHAR(20)     NOT NULL,
    "Price"            NUMERIC(18,8)   NOT NULL,
    "Quantity"         NUMERIC(28,8)   NOT NULL,
    "QuoteQuantity"    NUMERIC(28,8)   NOT NULL,
    "TradeTime"        BIGINT          NOT NULL,
    "IsBuyerMaker"     BOOLEAN         NOT NULL,
    "IsBestMatch"      BOOLEAN         NOT NULL,
    "OrderId"          BIGINT          NULL,
    "Commission"       NUMERIC(18,8)   NULL,
    "CommissionAsset"  VARCHAR(10)     NULL,
    "IsMyTrade"        BOOLEAN         NOT NULL DEFAULT false,
    "ProcessingStatus" VARCHAR(10)     NOT NULL DEFAULT 'new',
    PRIMARY KEY ("TradeId", "Symbol", "TradeTime")
) PARTITION BY RANGE ("TradeTime");

-- ============================================================
-- 3. Indexes on parent — auto-inherited by every new partition
-- ============================================================
CREATE INDEX ix_trades_symbol_tradetime
    ON public."Trades" ("Symbol", "TradeTime" DESC);

CREATE INDEX ix_trades_processingstatus
    ON public."Trades" ("ProcessingStatus")
    WHERE "ProcessingStatus" = 'new';

-- ============================================================
-- 4. Update sp_bulk_insert_trades — ON CONFLICT uses new PK
-- ============================================================
CREATE OR REPLACE FUNCTION public.sp_bulk_insert_trades(
    p_trade_ids       BIGINT[],
    p_symbols         VARCHAR[],
    p_prices          NUMERIC[],
    p_quantities      NUMERIC[],
    p_quote_quantities NUMERIC[],
    p_trade_times     BIGINT[],
    p_is_buyer_makers BOOLEAN[],
    p_is_best_matches BOOLEAN[]
) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO public."Trades" (
        "TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity",
        "TradeTime", "IsBuyerMaker", "IsBestMatch"
    )
    SELECT * FROM UNNEST(
        p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities,
        p_trade_times, p_is_buyer_makers, p_is_best_matches
    )
    ON CONFLICT ("TradeId", "Symbol", "TradeTime") DO NOTHING;
END;
$$;

-- ============================================================
-- 5. sp_ensure_trades_partition(target_time BIGINT)
--    Creates the monthly partition for target_time if it doesn't exist.
--    Called before every bulk insert to guarantee the partition is ready.
-- ============================================================
CREATE OR REPLACE FUNCTION public.sp_ensure_trades_partition(target_time BIGINT)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    month_start TIMESTAMP;
    month_end   TIMESTAMP;
    part_name   TEXT;
    from_ms     BIGINT;
    to_ms       BIGINT;
BEGIN
    month_start := DATE_TRUNC('month', TO_TIMESTAMP(target_time / 1000.0) AT TIME ZONE 'UTC');
    month_end   := month_start + INTERVAL '1 month';
    part_name   := 'Trades_' || TO_CHAR(month_start, 'YYYY_MM');
    from_ms     := EXTRACT(EPOCH FROM month_start)::BIGINT * 1000;
    to_ms       := EXTRACT(EPOCH FROM month_end)::BIGINT * 1000;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = part_name AND n.nspname = 'public') THEN
        EXECUTE format(
            'CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
            part_name, from_ms, to_ms
        );
    END IF;
END;
$$;

-- ============================================================
-- 6. sp_rotate_trades_partition()
--    - Ensures current month partition exists
--    - Pre-creates next month partition (so we never miss midnight boundary)
--    - Drops the partition that is 13 months older than current month
--    Run daily via Hangfire (PartitionMaintenanceWorker).
--    NOT called during dev initial load — enable only on prod.
-- ============================================================
CREATE OR REPLACE FUNCTION public.sp_rotate_trades_partition()
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    current_month TIMESTAMP;
    target_month  TIMESTAMP;
    part_name     TEXT;
    from_ms       BIGINT;
    to_ms         BIGINT;
    old_month     TIMESTAMP;
    old_part_name TEXT;
BEGIN
    current_month := DATE_TRUNC('month', NOW() AT TIME ZONE 'UTC');

    -- Create current month and next month partitions if needed
    FOR target_month IN
        SELECT m FROM generate_series(current_month, current_month + INTERVAL '1 month', INTERVAL '1 month') AS m
    LOOP
        part_name := 'Trades_' || TO_CHAR(target_month, 'YYYY_MM');
        from_ms   := EXTRACT(EPOCH FROM target_month)::BIGINT * 1000;
        to_ms     := EXTRACT(EPOCH FROM (target_month + INTERVAL '1 month'))::BIGINT * 1000;

        IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                       WHERE c.relname = part_name AND n.nspname = 'public') THEN
            EXECUTE format(
                'CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
                part_name, from_ms, to_ms
            );
        END IF;
    END LOOP;

    -- Drop partition that is 13 months older than current month
    old_month     := current_month - INTERVAL '13 months';
    old_part_name := 'Trades_' || TO_CHAR(old_month, 'YYYY_MM');

    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE c.relname = old_part_name AND n.nspname = 'public') THEN
        EXECUTE format('ALTER TABLE public."Trades" DETACH PARTITION public.%I', old_part_name);
        EXECUTE format('DROP TABLE public.%I', old_part_name);
        RAISE NOTICE 'Dropped old partition: %', old_part_name;
    END IF;
END;
$$;

-- ============================================================
-- 7. Pre-create monthly partitions: Jan 2025 — Dec 2026
--    Covers the full initial historical load period.
-- ============================================================
DO $$
DECLARE
    d        DATE := '2025-01-01';
    end_date DATE := '2027-01-01';
    from_ms  BIGINT;
    to_ms    BIGINT;
    pname    TEXT;
BEGIN
    WHILE d < end_date LOOP
        from_ms := EXTRACT(EPOCH FROM d)::BIGINT * 1000;
        to_ms   := EXTRACT(EPOCH FROM (d + INTERVAL '1 month'))::BIGINT * 1000;
        pname   := 'Trades_' || TO_CHAR(d, 'YYYY_MM');

        IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                       WHERE c.relname = pname AND n.nspname = 'public') THEN
            EXECUTE format(
                'CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
                pname, from_ms, to_ms
            );
            RAISE NOTICE 'Created partition: %', pname;
        END IF;

        d := d + INTERVAL '1 month';
    END LOOP;
END;
$$;
