DO $$
DECLARE
    -- УСТАНОВИ ЗДЕСЬ ДАТУ, С КОТОРОЙ НАЧНЕТСЯ ТВОЯ ИСТОРИЯ
    start_date_literal TEXT := '2025-01-01'; -- <-- ИЗМЕНИ ЗДЕСЬ
    
    start_date TIMESTAMP := to_timestamp(start_date_literal, 'YYYY-MM-DD') AT TIME ZONE 'UTC';
    start_timestamp BIGINT;
BEGIN
    start_timestamp := (EXTRACT(EPOCH FROM start_date) * 1000)::BIGINT;

    INSERT INTO public."Processing_Watermarks" ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES
    ('OhlcvAggregator', start_timestamp, 'Idle', NOW()),
    ('FeatureCalculator', start_timestamp, 'Idle', NOW());
    -- ON CONFLICT ... (оставляем)
END $$;