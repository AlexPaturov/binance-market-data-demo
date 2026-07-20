-- Процедуры конвейера сообщают о появлении работы через NOTIFY.
--
-- Обработка была привязана к тику таймера (`Cron.Minutely()`), а не к появлению работы:
-- пачка фиксированного размера в одной транзакции не укладывалась в командный таймаут,
-- откатывалась целиком, и через минуту таймер запускал ровно то же самое. Событие в системе
-- уже было — строка в `DirtyMinutes` от вставки тиков; не хватало доставки этого события
-- потребителю. Здесь появляется доставка, потребитель — в `OhlcvAggregationService`.
--
-- Каналы:
--   `dirty_minutes` — вставка тиков зарегистрировала новые грязные минуты;
--   `candles_new`   — агрегация пересчитала свечи, по ним устарели индикаторы.
--
-- Уведомление шлётся только когда работа действительно появилась: повторный импорт того же
-- архива упирается в ON CONFLICT DO NOTHING, ничего не пачкает и потребителя не будит.
-- Одинаковые уведомления в пределах транзакции Postgres схлопывает сам, поэтому пачка на
-- 10 тысяч тиков даёт одно сообщение, а не тысячу.
--
-- Уведомление доставляется получателям при COMMIT: подписчик увидит его только после того,
-- как работа стала видимой в таблице. Обратного порядка (сигнал раньше данных) быть не может.
--
-- Обратно совместимо: старый воркер каналы не слушает, для него процедуры не меняются.
-- Поэтому миграция применяется до деплоя воркера, без двухфазности.
--
--   psql -U bindatacoll -d market_analytics -f 010_notify_pipeline_events.sql

BEGIN;

-- ============================================================================
--  Вставка тиков будит агрегатор
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
DECLARE
    v_dirty INT := 0;
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
    ),
    dirty AS (
        INSERT INTO public."DirtyMinutes" ("Symbol", "OpenTime")
        SELECT DISTINCT "Symbol", ("TradeTime" / 60000) * 60000
        FROM inserted
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    SELECT count(*) INTO v_dirty FROM dirty;

    IF v_dirty > 0 THEN
        PERFORM pg_notify('dirty_minutes', '');
    END IF;
END;
$$;

-- ============================================================================
--  Агрегация будит расчёт индикаторов
-- ============================================================================

-- Тело — версия миграции 007 (LATERAL-скан, разбор от свежих минут к старым). Добавлено
-- уведомление в конце; возвращаемое значение сменило смысл (см. ниже). Разбор ведётся
-- кусками по вызову: каждый вызов — своя транзакция и свой коммит, поэтому обрыв посреди
-- разбора не обнуляет прогресс.
--
-- ВОЗВРАТ: сколько минут снято с очереди, а не сколько построено свечей. Потребитель пьёт
-- очередь до дна и по возврату решает, осталась ли работа. Минута без тиков (их удалила
-- ротация партиций) свечи не даёт, но с очереди уходит — по числу свечей такой кусок
-- выглядел бы как пустая очередь, и разбор останавливался бы на нём, не дойдя до дна.
CREATE OR REPLACE FUNCTION public.sp_aggregate_dirty_minutes(
    p_max_minutes integer DEFAULT 10000
) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_minutes INT := 0;
    v_candles INT := 0;
    v_max_time BIGINT;
    m BIGINT;
BEGIN
    CREATE TEMP TABLE batch ON COMMIT DROP AS
    SELECT "Symbol", "OpenTime"
    FROM public."DirtyMinutes"
    ORDER BY "OpenTime" DESC
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
    -- участвовали в прошлом расчёте: результат не зависит от порядка прихода данных.
    --
    -- LATERAL обязателен, см. шапку 007_aggregate_lateral_scan.sql. MIN/MAX по массиву
    -- вместо array_agg(ORDER BY): цена первой и последней сделки берутся потоковым
    -- агрегатом, без сортировки тиков минуты.
    INSERT INTO public."Ohlcv_1min"
        ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT
        b."Symbol",
        b."OpenTime",
        c."OpenPrice", c."HighPrice", c."LowPrice", c."ClosePrice", c."Volume",
        'new'
    FROM batch b
    CROSS JOIN LATERAL (
        SELECT
            (MIN(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "OpenPrice",
            MAX(t."Price")                                                                          AS "HighPrice",
            MIN(t."Price")                                                                          AS "LowPrice",
            (MAX(ARRAY[t."TradeTime"::numeric, t."TradeId"::numeric, t."Price"]))[3]::numeric(18,8) AS "ClosePrice",
            SUM(t."Quantity")                                                                       AS "Volume"
        FROM public."Trades" t
        WHERE t."Symbol" = b."Symbol"
          AND t."TradeTime" >= b."OpenTime"
          AND t."TradeTime" <  b."OpenTime" + 60000
    ) c
    -- Минута без тиков (например, тики удалила ротация партиций): свечи не будет,
    -- но из очереди минута всё равно уйдёт.
    WHERE c."Volume" IS NOT NULL
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

    GET DIAGNOSTICS v_minutes = ROW_COUNT;

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

    -- Свечи пересчитаны → индикаторы по ним устарели: будим расчёт фич.
    IF v_candles > 0 THEN
        PERFORM pg_notify('candles_new', '');
    END IF;

    RETURN v_minutes;
END;
$$;

COMMIT;
