-- Гистерезис при снятии пары с отслеживания.
--
-- Раньше пара, отсутствующая в текущем скане, немедленно получала IsActive = FALSE.
-- Объём у пар вблизи порога в $10M колеблется день ото дня, поэтому пара, просевшая
-- под порог на один скан, теряла сбор данных до следующего попадания в ТОП — то есть
-- в истории появлялась дыра ровно там, где сбор должен был продолжаться.
--
-- Теперь у пары есть счётчик подряд идущих пропусков: она деактивируется только после
-- p_max_missed_scans сканов подряд без попадания в ТОП. Скан ежедневный, порог 3 —
-- значит трёхдневное окно терпимости к колебаниям объёма.
--
--   psql -U bindatacoll -d market_analytics -f 005_tracked_symbols_hysteresis.sql

BEGIN;

ALTER TABLE public."TrackedSymbols"
    ADD COLUMN IF NOT EXISTS "MissedScans" integer DEFAULT 0 NOT NULL;

-- Сигнатура меняется (добавился параметр с DEFAULT), поэтому старую функцию нужно
-- удалить: иначе вызов с одним аргументом стал бы неоднозначным.
DROP FUNCTION IF EXISTS public.sp_update_tracked_symbols(character varying[]);

CREATE FUNCTION public.sp_update_tracked_symbols(
    p_symbols           character varying[],
    p_max_missed_scans  integer DEFAULT 3
) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Пара пропущена этим сканом: наращиваем счётчик и снимаем с отслеживания,
    -- только когда пропусков накопилось p_max_missed_scans подряд.
    UPDATE public."TrackedSymbols"
    SET "MissedScans" = "MissedScans" + 1,
        "IsActive"    = ("MissedScans" + 1) < p_max_missed_scans
    WHERE "IsActive" = TRUE
      AND "Symbol" <> ALL(p_symbols);

    -- Пара в ТОПе: активна, счётчик пропусков сброшен (пропуски должны идти подряд).
    INSERT INTO public."TrackedSymbols" ("Symbol", "IsActive", "LastScanned", "MissedScans")
    SELECT symbol, TRUE, NOW(), 0 FROM UNNEST(p_symbols) AS u(symbol)
    ON CONFLICT ("Symbol") DO UPDATE
    SET "IsActive"    = TRUE,
        "LastScanned" = NOW(),
        "MissedScans" = 0;
END;
$$;

COMMIT;
