CREATE OR REPLACE FUNCTION public.sp_process_features()
RETURNS VOID AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
BEGIN
    -- 1. Получаем "вотермарку" для этого процесса
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'FeatureCalculator';

    -- 2. Находим конец "окна" обработки - последнюю новую свечу
    SELECT MAX("OpenTime") INTO end_timestamp
    FROM public."Ohlcv_1min"
    WHERE "ProcessingStatus" = 'new' AND "OpenTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    -- 3. Помечаем свечи в нашем "окне" как обработанные
    -- Мы делаем это в начале, чтобы избежать повторной обработки в параллельных воркерах (задел на будущее)
    UPDATE public."Ohlcv_1min"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new'
      AND "OpenTime" >= start_timestamp
      AND "OpenTime" <= end_timestamp;

    -- 4. Сдвигаем "вотермарку"
    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'FeatureCalculator';

END;
$$ LANGUAGE plpgsql;

SELECT 'Функция sp_process_features для FeatureCalculator успешно создана.' AS "Статус";

-- Важное отличие: Эта функция не рассчитывает индикаторы. 
-- Расчеты остаются в C#. Она просто управляет состоянием: находит "окно" новых данных, помечает их как обработанные и сдвигает вотермарку.