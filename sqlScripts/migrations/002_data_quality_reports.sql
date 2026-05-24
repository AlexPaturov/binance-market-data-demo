-- Migration 002: DataQualityReports table
-- Stores per-symbol per-month data integrity check results (Layer 1 checks).
-- Apply AFTER 001_partition_trades.sql.

CREATE TABLE IF NOT EXISTS public."DataQualityReports" (
    "Id"                SERIAL          PRIMARY KEY,
    "Symbol"            VARCHAR(20)     NOT NULL,
    "PeriodMonth"       DATE            NOT NULL,
    "TradeCount"        BIGINT          NOT NULL DEFAULT 0,
    "GapCount"          INT             NOT NULL DEFAULT 0,
    "InvalidPriceCount" INT             NOT NULL DEFAULT 0,
    "OutlierCount"      INT             NOT NULL DEFAULT 0,
    "Status"            VARCHAR(10)     NOT NULL DEFAULT 'ok',
    "CheckedAt"         TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_dqr_symbol_month
    ON public."DataQualityReports" ("Symbol", "PeriodMonth");

CREATE INDEX IF NOT EXISTS ix_dqr_status
    ON public."DataQualityReports" ("Status") WHERE "Status" <> 'ok';

CREATE INDEX IF NOT EXISTS ix_dqr_checked_at
    ON public."DataQualityReports" ("CheckedAt" DESC);
