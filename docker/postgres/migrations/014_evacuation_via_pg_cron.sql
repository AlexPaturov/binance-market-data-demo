-- Эвакуация партиций уходит из приложения в pg_cron; функция сама гасит наложение.
--
-- Раньше эвакуацию запускала Hangfire-джоба воркера с командным таймаутом 3600 с. Таймаут
-- был ошибкой: копия месяца ограничена его размером, а не константой — 108-гигабайтный
-- февраль под конкурентным импортом в час не уложился, транзакция откатилась, ~85 ГБ
-- скопированного впустую (инцидент 17.07.2026). Плюс сам запуск через Hangfire — лишний
-- слой (задача, пул воркеров) для операции «раз в месяц».
--
-- Теперь эвакуацию планирует сама БД через pg_cron (см. init/04_tablespace_and_cron.sql):
--   SELECT cron.schedule('evacuate-cold-partitions','0 * * * *',
--                        $$SELECT public.sp_evacuate_next_cold_partition()$$);
-- Ни приложения в пути данных, ни удерживаемого соединения, ни таймаута.
--
-- Наложение запусков (копия дольше часового тика) гасит сама функция транзакционным
-- advisory-локом: второй запуск не берёт лок и тихо выходит, параллельного переезда нет.
--
-- pg_cron ставится из образа (docker/postgres/Dockerfile, postgresql-16-cron) и грузится
-- через shared_preload_libraries; cron.database_name = market_analytics. На существующей
-- БД (без pg_cron/расширения) эта миграция обновляет только тело функции — расписание
-- добавляется вручную после установки расширения. На чистой инициализации всё делают
-- init-скрипты.
--
--   psql -U bindatacoll -d market_analytics -f 014_evacuation_via_pg_cron.sql

BEGIN;

CREATE OR REPLACE FUNCTION public.sp_evacuate_next_cold_partition()
RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_suffix TEXT;
    v_part TEXT;
    v_month TIMESTAMP;
    v_start_ms BIGINT;
    v_end_ms BIGINT;
    v_has_data BOOLEAN;
    v_index RECORD;
BEGIN
    -- Защита от наложения запусков — внутри функции, а не в планировщике: копия месяца
    -- (50–150 ГБ) идёт дольше часового тика pg_cron, и второй запуск не должен начать
    -- параллельный переезд. Транзакционный advisory-lock снимается сам на COMMIT/ROLLBACK,
    -- ручного освобождения не требует. Не взяли — копия уже идёт, тихо выходим.
    IF NOT pg_try_advisory_xact_lock(hashtext('sp_evacuate_next_cold_partition')) THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_tablespace WHERE spcname = 'cold') THEN
        RETURN NULL;
    END IF;

    SET LOCAL lock_timeout = '60s';

    -- Кандидаты — партиции Trades в горячем (базовом) пространстве за прошедшие
    -- месяцы, от старых к новым. reltablespace = 0 — базовое пространство БД (SSD).
    -- Перебор, а не первый кандидат: незакрытый месяц (докачивается) не должен
    -- запирать на SSD более поздние, уже закрытые.
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
        v_start_ms := EXTRACT(EPOCH FROM v_month)::BIGINT * 1000;
        v_end_ms := EXTRACT(EPOCH FROM v_month + INTERVAL '1 month')::BIGINT * 1000;

        -- Пустую партицию возить незачем: партиции создаются впрок на месяцы вперёд.
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I)', v_part) INTO v_has_data;
        IF NOT v_has_data THEN
            CONTINUE;
        END IF;

        -- Месяц ещё не закрыт: агрегация будет читать его тики — не трогаем.
        IF EXISTS (
            SELECT 1 FROM public."DirtyMinutes"
            WHERE "OpenTime" >= v_start_ms AND "OpenTime" < v_end_ms)
        THEN
            CONTINUE;
        END IF;

        -- Фичи месяца не досчитаны: свечи месяца ещё могут пересчитаться и снова
        -- запачкать минуты — подождём.
        IF EXISTS (
            SELECT 1 FROM public."Ohlcv_1min"
            WHERE "OpenTime" >= v_start_ms AND "OpenTime" < v_end_ms
              AND "ProcessingStatus" <> 'processed')
        THEN
            CONTINUE;
        END IF;

        EXECUTE format('ALTER TABLE public.%I SET TABLESPACE cold', v_part);

        -- SET TABLESPACE у таблицы НЕ переносит её индексы — каждый переезжает отдельно.
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
