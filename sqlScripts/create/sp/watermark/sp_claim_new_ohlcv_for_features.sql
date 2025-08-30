CREATE OR REPLACE FUNCTION public.sp_claim_new_ohlcv_for_features(
    p_batch_size INT
)
RETURNS TABLE("Symbol" VARCHAR(20), "OpenTime" BIGINT, "OpenPrice" DECIMAL, "HighPrice" DECIMAL, "LowPrice" DECIMAL, "ClosePrice" DECIMAL, "Volume" DECIMAL) AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
BEGIN
    LOCK TABLE public."Processing_Watermarks" IN EXCLUSIVE MODE;

    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'FeatureCalculator';

    SELECT MAX(t."OpenTime") INTO end_timestamp
    FROM public."Ohlcv_1min" t
    WHERE t."ProcessingStatus" = 'new' AND t."OpenTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;
    
    RETURN QUERY
    WITH ClaimedRows AS (
        SELECT t_inner."Symbol", t_inner."OpenTime"
        FROM public."Ohlcv_1min" t_inner
        WHERE t_inner."ProcessingStatus" = 'new'
          AND t_inner."OpenTime" >= start_timestamp
          AND t_inner."OpenTime" <= end_timestamp
        ORDER BY t_inner."OpenTime"
        LIMIT p_batch_size
        FOR UPDATE SKIP LOCKED
    )
    UPDATE public."Ohlcv_1min" as t_outer -- <-- 1. Даем псевдоним
    SET "ProcessingStatus" = 'processing'
    WHERE (t_outer."Symbol", t_outer."OpenTime") IN (SELECT cr."Symbol", cr."OpenTime" FROM ClaimedRows cr)
    -- --- ИСПРАВЛЕНИЕ ЗДЕСЬ ---
    -- 2. Явно указываем, что возвращаем столбцы из обновляемой таблицы
    RETURNING t_outer."Symbol", t_outer."OpenTime", t_outer."OpenPrice", t_outer."HighPrice", t_outer."LowPrice", t_outer."ClosePrice", t_outer."Volume";

    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'FeatureCalculator';
END;
$$ LANGUAGE plpgsql;