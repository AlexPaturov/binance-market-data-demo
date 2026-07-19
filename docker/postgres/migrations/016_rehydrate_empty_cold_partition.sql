-- Пустая партиция на холодном диске возвращается на горячий перед вставкой.
--
-- Снос данных (TRUNCATE) очищает строки, но табличное пространство партиции не меняет:
-- месяц, эвакуированный на cold, после сноса остаётся на cold пустым. Повторный импорт
-- этого месяца шёл на медленный USB-HDD мимо всего смысла тиринга (январь, 19.07.2026:
-- 24 ГБ вставки на внешний диск).
--
-- Фикс — в sp_ensure_month_partitions, потому что она стоит на пути КАЖДОЙ вставки
-- (BulkInsertAsync -> sp_ensure_trades_partition -> сюда, плюс агрегация и upsert фич):
-- если партиция Trades целевого месяца лежит на cold и ПУСТА — вернуть её на горячее
-- пространство. Перенос пустой партиции мгновенный (копировать нечего), поэтому цена
-- на пути вставки — один запрос к каталогу.
--
-- Непустые партиции на cold не трогаются: это закрытые месяцы, их место — холодный диск.
--
--   psql -U bindatacoll -d market_analytics -f 016_rehydrate_empty_cold_partition.sql

BEGIN;

CREATE OR REPLACE FUNCTION public.sp_rehydrate_if_empty_cold(p_table text)
RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_on_cold BOOLEAN;
    v_empty   BOOLEAN;
    v_index   RECORD;
BEGIN
    SELECT (t.spcname = 'cold') INTO v_on_cold
    FROM pg_class c
    JOIN pg_tablespace t ON t.oid = c.reltablespace
    WHERE c.relname = p_table AND c.relkind = 'r';

    IF NOT COALESCE(v_on_cold, false) THEN
        RETURN;
    END IF;

    EXECUTE format('SELECT NOT EXISTS (SELECT 1 FROM public.%I)', p_table) INTO v_empty;
    IF NOT v_empty THEN
        RETURN;
    END IF;

    EXECUTE format('ALTER TABLE public.%I SET TABLESPACE pg_default', p_table);
    FOR v_index IN
        SELECT indexname FROM pg_indexes
        WHERE schemaname = 'public' AND tablename = p_table
    LOOP
        EXECUTE format('ALTER INDEX public.%I SET TABLESPACE pg_default', v_index.indexname);
    END LOOP;

    RAISE NOTICE 'Пустая партиция % возвращена с cold на горячее пространство.', p_table;
END;
$$;

-- В конец sp_ensure_month_partitions добавлен вызов rehydrate для Trades-партиции месяца.
-- Тело функции — текущее (миграции 002/005 + барьер ретенции), меняется только хвост.
DO $$
DECLARE
    v_src TEXT;
BEGIN
    SELECT pg_get_functiondef(oid) INTO v_src
    FROM pg_proc WHERE proname = 'sp_ensure_month_partitions';

    IF v_src LIKE '%sp_rehydrate_if_empty_cold%' THEN
        RETURN; -- уже пропатчена
    END IF;

    -- Вставляем вызов перед финальным END функции.
    v_src := regexp_replace(
        v_src,
        'END;?\s*\$function\$',
        E'    PERFORM public.sp_rehydrate_if_empty_cold(''Trades_'' || suffix);\nEND;\n$function$');

    EXECUTE v_src;
END;
$$;

COMMIT;
