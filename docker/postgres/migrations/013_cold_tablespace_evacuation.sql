-- Эвакуация закрытых месяцев Trades на холодное табличное пространство.
--
-- Конвейер живёт случайным доступом, и его цена — IOPS. Внутренний SSD сервера даёт
-- десятки тысяч IOPS, внешний USB-HDD — ~200 (замер 16.07.2026: %util 84–89 при
-- 16–21 МБ/с — шпиндель насыщен позиционированием, а не полосой). Поэтому данные
-- разложены по силе дисков: PGDATA с горячими партициями — на SSD, а закрытые месяцы
-- тиков переезжают на HDD одним последовательным копированием — единственным видом
-- нагрузки, в котором HDD хорош.
--
-- Переезжают только партиции "Trades" — это гигабайты и источник случайных чтений.
-- Свечи и индикаторы остаются на SSD навсегда: они на три порядка меньше тиков,
-- и по ним ходят график и ML-выборки.
--
-- Месяц считается закрытым, когда он прошёл, в нём нет грязных минут и все его свечи
-- обработаны расчётом фич. Поздний тик в эвакуированный месяц не ломает ничего:
-- вставка и пересчёт свечи работают с холодной партицией, просто медленнее.
--
-- Пространство `cold` создаётся руками при миграции сервера (каталог должен
-- существовать и принадлежать postgres):
--
--   CREATE TABLESPACE cold LOCATION '/mnt/pg_tablespaces/cold';
--
-- Без пространства `cold` функция молча бездействует — dev-окружения и тесты,
-- которым эвакуация не нужна, не обязаны его создавать.
--
--   psql -U bindatacoll -d market_analytics -f 013_cold_tablespace_evacuation.sql

BEGIN;

/*
 * Переносит на пространство `cold` ОДНУ партицию Trades — старейший закрытый месяц,
 * ещё лежащий в горячем пространстве. Возвращает имя перенесённой партиции,
 * NULL — переносить нечего.
 *
 * Один вызов — одна партиция — одна транзакция: перенос 50–150 ГБ занимает десятки
 * минут, и дробление по вызовам не даёт одному гиганту откатить работу остальных
 * (тот же принцип, что у sp_aggregate_dirty_minutes).
 *
 * SET TABLESPACE держит ACCESS EXCLUSIVE на партицию на всё время копирования.
 * Для закрытого месяца конкуренты — только поздние тики, поэтому блокировка
 * безопасна; lock_timeout страхует от встречи с долгим читателем: не дождались —
 * вернёмся в следующий запуск.
 */
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
