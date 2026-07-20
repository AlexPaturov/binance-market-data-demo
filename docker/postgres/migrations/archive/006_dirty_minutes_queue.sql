-- Очередь «грязных минут» вместо статус-колонки у тиков.
--
-- Агрегатор находил работу по `Trades."ProcessingStatus" = 'new'`, а в конце помечал
-- разобранные тики `'processed'`. Замер на реальных данных (окно 6 часов, 1.4 млн тиков):
-- пересчёт свечей — 1.7 с, пометка статуса — 11.8 с. Каждый тик переписывался целиком
-- (MVCC-перезапись строки + три индекса), таблица раздувалась вдвое до autovacuum.
--
-- 13.07.2026 это положило конвейер: импорт архивов оставил 106 млн тиков со статусом
-- 'new', UPDATE по такому объёму перестал укладываться в командный таймаут (600 с),
-- транзакция откатывалась целиком — и агрегатор часами повторял один и тот же откат,
-- не сдвинувшись ни на свечу.
--
-- Теперь затронутые минуты регистрирует сама вставка: `sp_bulk_insert_trades` дописывает
-- в `DirtyMinutes` минуты тех строк, которые реально легли в таблицу (несколько строк на
-- пачку в 10 тысяч тиков). Агрегатор забирает пачку минут, пересчитывает по ним свечи и
-- удаляет минуты из очереди. UPDATE по `Trades` исчезает совсем — вместе с bloat'ом.
--
-- Свойство, ради которого делалась статус-агрегация (миграция 003), сохраняется: тик,
-- приехавший «позади» уже посчитанного участка, помечает свою минуту грязной, и свеча
-- пересчитывается целиком из всех тиков этой минуты. Порядок прихода данных по-прежнему
-- не важен, операция идемпотентна.
--
--   psql -U bindatacoll -d market_analytics -f 006_dirty_minutes_queue.sql

BEGIN;

-- ============================================================================
--  Очередь
-- ============================================================================

-- Не партиционируется: это состояние процесса, а не данные. В установившемся режиме
-- здесь десятки строк (по одной на пару за минуту), после импорта архивов — сотни тысяч.
CREATE TABLE IF NOT EXISTS public."DirtyMinutes" (
    "Symbol"    character varying(20) NOT NULL,
    "OpenTime"  bigint NOT NULL,              -- начало минуты, Unix-мс (сетка свечей)
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT "PK_DirtyMinutes" PRIMARY KEY ("Symbol", "OpenTime")
);

-- Разбираем очередь от старых минут к свежим — по этому индексу.
CREATE INDEX IF NOT EXISTS "IX_DirtyMinutes_OpenTime"
    ON public."DirtyMinutes" USING btree ("OpenTime");

-- Переносим в очередь работу, накопившуюся под старой схемой.
INSERT INTO public."DirtyMinutes" ("Symbol", "OpenTime")
SELECT DISTINCT "Symbol", ("TradeTime" / 60000) * 60000
FROM public."Trades"
WHERE "ProcessingStatus" = 'new'
ON CONFLICT DO NOTHING;

-- ============================================================================
--  Вставка тиков регистрирует затронутые минуты
-- ============================================================================

CREATE OR REPLACE FUNCTION public.sp_bulk_insert_trades(
    p_trade_ids bigint[],
    p_symbols character varying[],
    p_prices numeric[],
    p_quantities numeric[],
    p_quote_quantities numeric[],
    p_trade_times bigint[],
    p_is_buyer_makers boolean[],
    p_is_best_matches boolean[]
) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Грязными помечаем минуты только тех тиков, что действительно вставились (RETURNING).
    -- Повторный импорт того же архива упирается в ON CONFLICT DO NOTHING и очередь не пачкает:
    -- пересчитывать свечу, в которой ничего не изменилось, незачем.
    WITH inserted AS (
        INSERT INTO public."Trades" (
            "TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity",
            "TradeTime", "IsBuyerMaker", "IsBestMatch"
        )
        SELECT * FROM UNNEST(
            p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities,
            p_trade_times, p_is_buyer_makers, p_is_best_matches
        )
        ON CONFLICT ("TradeId", "Symbol", "TradeTime") DO NOTHING
        RETURNING "Symbol", "TradeTime"
    )
    INSERT INTO public."DirtyMinutes" ("Symbol", "OpenTime")
    SELECT DISTINCT "Symbol", ("TradeTime" / 60000) * 60000
    FROM inserted
    ON CONFLICT DO NOTHING;
END;
$$;

-- ============================================================================
--  Агрегация по очереди минут
-- ============================================================================

/*
 * Забирает пачку грязных минут, пересчитывает по ним свечи и удаляет минуты из очереди.
 * Возвращает количество пересчитанных свечей.
 *
 * Пачка блокируется FOR UPDATE SKIP LOCKED (ADR 0005): параллельный запуск возьмёт
 * следующие минуты, а не будет ждать. Минуты удаляются из очереди в той же транзакции,
 * что и запись свечей, — сбой откатывает и то, и другое, работа не теряется.
 */
CREATE OR REPLACE FUNCTION public.sp_aggregate_dirty_minutes(
    p_max_minutes integer DEFAULT 10000
) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_candles INT := 0;
    v_max_time BIGINT;
    m BIGINT;
BEGIN
    CREATE TEMP TABLE batch ON COMMIT DROP AS
    SELECT "Symbol", "OpenTime"
    FROM public."DirtyMinutes"
    ORDER BY "OpenTime"
    LIMIT p_max_minutes
    FOR UPDATE SKIP LOCKED;

    IF NOT EXISTS (SELECT 1 FROM batch) THEN
        RETURN 0;
    END IF;

    -- Партиции под месяцы пачки.
    FOR m IN
        SELECT DISTINCT (EXTRACT(EPOCH FROM DATE_TRUNC('month',
            TO_TIMESTAMP("OpenTime" / 1000.0) AT TIME ZONE 'UTC'))::BIGINT) * 1000
        FROM batch
    LOOP
        PERFORM public.sp_ensure_month_partitions(m);
    END LOOP;

    -- Свеча пересчитывается целиком из всех тиков своей минуты — включая те, что уже
    -- участвовали в прошлом расчёте. Поэтому докачанная дыра или архив, приехавший
    -- «позади», дают тот же результат, что и данные, пришедшие по порядку.
    --
    -- MIN/MAX по массиву вместо array_agg(ORDER BY): массивы сравниваются поэлементно,
    -- поэтому цена первой и последней сделки берутся потоковым агрегатом, без сортировки.
    INSERT INTO public."Ohlcv_1min"
        ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        t."Symbol",
        b."OpenTime",
        (MIN(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "OpenPrice",
        MAX(t."Price")                                                                          AS "HighPrice",
        MIN(t."Price")                                                                          AS "LowPrice",
        (MAX(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "ClosePrice",
        SUM(t."Quantity")                                                                       AS "Volume",
        'new'
    FROM batch b
    JOIN public."Trades" t
      ON t."Symbol" = b."Symbol"
     AND t."TradeTime" >= b."OpenTime"
     AND t."TradeTime" <  b."OpenTime" + 60000
    GROUP BY t."Symbol", b."OpenTime"
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "OpenPrice"  = EXCLUDED."OpenPrice",
        "HighPrice"  = EXCLUDED."HighPrice",
        "LowPrice"   = EXCLUDED."LowPrice",
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume"     = EXCLUDED."Volume",
        -- Свеча пересчитана → индикаторы по ней устарели, feature-pipeline возьмёт её заново.
        "ProcessingStatus" = 'new';

    GET DIAGNOSTICS v_candles = ROW_COUNT;

    DELETE FROM public."DirtyMinutes" d
    USING batch b
    WHERE d."Symbol" = b."Symbol"
      AND d."OpenTime" = b."OpenTime";

    SELECT MAX("OpenTime") INTO v_max_time FROM batch;

    -- Watermark — индикатор прогресса. Корректность на нём не держится: работу находит очередь.
    INSERT INTO public."Processing_Watermarks"
        ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES ('OhlcvAggregator', v_max_time + 60000, 'Pending', NOW())
    ON CONFLICT ("ProcessName") DO UPDATE SET
        "LastProcessedTimestamp" = GREATEST(
            public."Processing_Watermarks"."LastProcessedTimestamp", EXCLUDED."LastProcessedTimestamp"),
        "Status"         = 'Pending',
        "LastUpdate_UTC" = NOW();

    RETURN v_candles;
END;
$$;

DROP FUNCTION IF EXISTS public.sp_aggregate_new_trades(bigint);

-- ============================================================================
--  Статус тиков больше не нужен
-- ============================================================================

-- Колонка и частичный индекс по ней исчезают: работу находит очередь минут.
-- Заодно уходит и стоимость поддержки этого индекса на каждой вставке тика.
DROP INDEX IF EXISTS public.ix_trades_new_tradetime;

ALTER TABLE public."Trades" DROP COLUMN IF EXISTS "ProcessingStatus";

COMMIT;
