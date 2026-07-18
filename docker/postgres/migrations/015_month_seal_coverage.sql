-- Точный маркер «месяц закрыт» через журнал покрытия — вместо эвристики эвакуации.
--
-- Прежний страж эвакуации проверял «нет грязных минут + свечи processed». Эти условия
-- истинны и в паузе между пачками импорта, поэтому докачиваемый месяц ловился в затишье
-- и уезжал на холодный диск преждевременно (март, 18.07.2026). Момент dirty=0 не отвечает
-- на главный вопрос — «данные за месяц больше не приедут».
--
-- Теперь «закрыт» вычисляется явно из ДВУХ источников, которые видит только приложение:
--   - очередь импорта Hangfire (в другой БД) — «backfill не в полёте»;
--   - журнал покрытия ArchiveImportLog — какие (символ, день) реально импортированы.
-- Решение принимает воркер (ReconcileMonthSealsAsync) и печатает маркер MonthSeal.
-- pg_cron-эвакуация гейтится на MonthSeal — физический переезд остаётся в БД.
--
--   psql -U bindatacoll -d market_analytics -f 015_month_seal_coverage.sql

BEGIN;

-- ============================================================================
--  Журнал покрытия: какой (символ, день) импортирован из архива
-- ============================================================================
-- Ground truth «этот день у нас есть». Пишет CsvImportWorker при успешном импорте файла.
CREATE TABLE IF NOT EXISTS public."ArchiveImportLog" (
    "Symbol"     character varying(20) NOT NULL,
    "TradeDate"  date NOT NULL,
    "ImportedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_ArchiveImportLog" PRIMARY KEY ("Symbol", "TradeDate")
);

CREATE INDEX IF NOT EXISTS "IX_ArchiveImportLog_TradeDate"
    ON public."ArchiveImportLog" USING btree ("TradeDate");

-- ============================================================================
--  Маркер закрытого месяца: печатает воркер, читает эвакуация
-- ============================================================================
CREATE TABLE IF NOT EXISTS public."MonthSeal" (
    "PeriodMonth" date NOT NULL,
    "SealedAt"    timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_MonthSeal" PRIMARY KEY ("PeriodMonth")
);

-- ============================================================================
--  Проверка данных месяца (часть критерия, вычислимая в самой БД)
-- ============================================================================
/*
 * TRUE, если ДАННЫЕ месяца готовы: всё сагрегировано, все свечи обработаны и покрытие
 * по журналу сплошное. «Backfill не в полёте» проверяет воркер по очереди Hangfire —
 * сюда не входит (очередь в другой БД).
 *
 *   1. Нет DirtyMinutes за месяц — всё сагрегировано.
 *   2. Все свечи месяца 'processed' — обработаны (с индикаторами, миграция 011).
 *   3. Покрытие сплошное — у каждой пары дни в журнале за месяц идут без внутренних
 *      пропусков (count == max - min + 1). Ловит дыру внутри диапазона; пропущенный
 *      хвост (дни, которые не ставили на закачку) журналу не виден — это на операторе.
 *      Пустой журнал за месяц ⇒ покрытия нет ⇒ не закрыт.
 */
CREATE OR REPLACE FUNCTION public.fn_month_data_complete(p_month date)
RETURNS boolean
    LANGUAGE sql
    STABLE
    AS $$
    WITH bounds AS (
        SELECT "Symbol",
               min("TradeDate") AS lo,
               max("TradeDate") AS hi,
               count(*)         AS n
        FROM public."ArchiveImportLog"
        WHERE "TradeDate" >= p_month
          AND "TradeDate" <  (p_month + interval '1 month')
        GROUP BY "Symbol"
    )
    SELECT
        -- 3a. журнал за месяц не пуст
        EXISTS (SELECT 1 FROM bounds)
        -- 3b. у каждой пары покрытие сплошное
        AND NOT EXISTS (
            SELECT 1 FROM bounds WHERE n <> (hi - lo + 1))
        -- 1. всё сагрегировано
        AND NOT EXISTS (
            SELECT 1 FROM public."DirtyMinutes"
            WHERE "OpenTime" >= (EXTRACT(EPOCH FROM p_month)::bigint * 1000)
              AND "OpenTime" <  (EXTRACT(EPOCH FROM (p_month + interval '1 month'))::bigint * 1000))
        -- 2. все свечи обработаны
        AND NOT EXISTS (
            SELECT 1 FROM public."Ohlcv_1min"
            WHERE "OpenTime" >= (EXTRACT(EPOCH FROM p_month)::bigint * 1000)
              AND "OpenTime" <  (EXTRACT(EPOCH FROM (p_month + interval '1 month'))::bigint * 1000)
              AND "ProcessingStatus" <> 'processed');
$$;

-- ============================================================================
--  Эвакуация гейтится на маркере закрытого месяца
-- ============================================================================
CREATE OR REPLACE FUNCTION public.sp_evacuate_next_cold_partition()
RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_suffix TEXT;
    v_part TEXT;
    v_month DATE;
    v_has_data BOOLEAN;
    v_index RECORD;
BEGIN
    -- Защита от наложения запусков: копия месяца идёт дольше часового тика pg_cron.
    IF NOT pg_try_advisory_xact_lock(hashtext('sp_evacuate_next_cold_partition')) THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_tablespace WHERE spcname = 'cold') THEN
        RETURN NULL;
    END IF;

    SET LOCAL lock_timeout = '60s';

    -- Кандидаты — партиции Trades в горячем (базовом) пространстве за прошедшие месяцы,
    -- от старых к новым. Незакрытый месяц не должен запирать более поздние закрытые.
    FOR v_suffix IN
        SELECT substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'public."Trades"'::regclass
          AND c.reltablespace = 0
          AND TO_DATE(substring(c.relname FROM 'Trades_(\d{4}_\d{2})'), 'YYYY_MM')
              < DATE_TRUNC('month', NOW() AT TIME ZONE 'UTC')
        ORDER BY substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
    LOOP
        v_part := 'Trades_' || v_suffix;
        v_month := TO_DATE(v_suffix, 'YYYY_MM');

        -- Пустую партицию возить незачем: партиции создаются впрок на месяцы вперёд.
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I)', v_part) INTO v_has_data;
        IF NOT v_has_data THEN
            CONTINUE;
        END IF;

        -- Месяц закрыт: маркер от воркера (backfill не в полёте + покрытие) плюс
        -- перепроверка данных на момент эвакуации (страховка от устаревшего маркера).
        IF NOT EXISTS (SELECT 1 FROM public."MonthSeal" WHERE "PeriodMonth" = v_month)
           OR NOT public.fn_month_data_complete(v_month)
        THEN
            CONTINUE;
        END IF;

        EXECUTE format('ALTER TABLE public.%I SET TABLESPACE cold', v_part);

        FOR v_index IN
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = v_part
        LOOP
            EXECUTE format('ALTER INDEX public.%I SET TABLESPACE cold', v_index.indexname);
        END LOOP;

        RETURN v_part;
    END LOOP;

    RETURN NULL;
END;
$$;

COMMIT;
