-- Фаза 1 из 2: новая сигнатура sp_upsert_ohlcv_features без MA-параметров.
--
-- Решение: колонки MA_1051200 (двухлетняя SMA в минутных барах) и MA_201600 удаляются
-- из конвейера фич. Значение MA_1051200 требует двух лет непрерывной минутной истории —
-- при старте истории с 2026-01 первое значение появилось бы не раньше 2028 года, а
-- ретенция по размеру диска (окно 19–30 месяцев, ADR 0007) может срезать историю раньше,
-- чем оно появится. Расчёт при этом заставлял конвейер таскать всю историю символа
-- каждый цикл. Долгие MA для ML предполагается считать на дневных свечах — это отдельная
-- задача с отдельной таблицей, а не минутные колонки.
--
-- Двухфазность: сигнатура функции меняется (8 параметров → 6), а воркер продолжает писать
-- фичи во время выката. Эта миграция добавляет 6-параметровую функцию РЯДОМ со старой
-- (вызовы различаются числом аргументов — неоднозначности нет): старый воркер работает
-- до самого рестарта, новый — сразу после. Фаза 2 (009) применяется ПОСЛЕ деплоя воркера
-- и удаляет старую функцию вместе с колонками.
--
--   psql -U bindatacoll -d market_analytics -f 008_features_slim_upsert.sql

BEGIN;

CREATE OR REPLACE FUNCTION public.sp_upsert_ohlcv_features(
    p_symbols character varying[],
    p_open_times bigint[],
    p_rsi_14 numeric[],
    p_macd_signals numeric[],
    p_macd_hists numeric[],
    p_cvds numeric[]
) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    month_ms BIGINT;
BEGIN
    CREATE TEMP TABLE NewFeatures ON COMMIT DROP AS
    SELECT * FROM UNNEST(
        p_symbols, p_open_times, p_rsi_14, p_macd_signals, p_macd_hists, p_cvds
    ) AS t(
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "CVD"
    );

    FOR month_ms IN SELECT DISTINCT "OpenTime" FROM NewFeatures LOOP
        PERFORM public.sp_ensure_month_partitions(month_ms);
    END LOOP;

    INSERT INTO public."Ohlcv_Features" (
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist", "CVD"
    )
    SELECT * FROM NewFeatures
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET
        "RSI_14" = EXCLUDED."RSI_14",
        "MACD_Signal" = EXCLUDED."MACD_Signal",
        "MACD_Hist" = EXCLUDED."MACD_Hist",
        "CVD" = EXCLUDED."CVD";
END;
$$;

COMMIT;
