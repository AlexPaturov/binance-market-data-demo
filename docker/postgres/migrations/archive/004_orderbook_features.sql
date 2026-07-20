-- Поминутные фичи стакана для ML.
--
-- Сырой L2 НЕ храним: полная глубина с диффами раз в секунду по 40 парам — это
-- ~190 ГБ/месяц даже при экономной схеме (событие с уровнями в массивах), что съедает
-- бюджет, отведённый под тики. Фичи считаются на лету из книги в памяти и пишутся
-- поминутными агрегатами — ~0.4 ГБ/месяц.
--
-- Истории у этой таблицы не будет и не может быть: архивов глубины по споту у Binance
-- нет (в data/spot/daily/ только aggTrades, klines, trades; bookDepth есть лишь для
-- фьючерсов). Данные идут с момента запуска коллектора.
--
-- См. docs/adr/0007-size-based-retention-and-unified-partitioning.md
--
--   psql -U bindatacoll -d market_analytics -f 004_orderbook_features.sql

BEGIN;

CREATE TABLE IF NOT EXISTS public."OrderBook_Features" (
    "Symbol"   character varying(20) NOT NULL,
    "OpenTime" bigint NOT NULL,              -- начало минуты, Unix-мс (та же сетка, что у свечей)

    -- Цена и спред
    "MidPrice"  numeric(18,8) NOT NULL,
    "BestBid"   numeric(18,8) NOT NULL,
    "BestAsk"   numeric(18,8) NOT NULL,
    "SpreadAbs" numeric(18,8) NOT NULL,      -- ask - bid
    "SpreadBps" numeric(12,4) NOT NULL,      -- спред в базисных пунктах от mid

    -- Дисбаланс книги по топ-20 уровням: (bid - ask) / (bid + ask), от -1 до +1.
    -- Классический предиктор краткосрочного движения.
    "Imbalance" numeric(10,6) NOT NULL,

    -- Ликвидность вблизи цены — «толщина» рынка на трёх горизонтах
    "BidDepth01" numeric(28,8) NOT NULL,     -- объём заявок в пределах -0.1% от mid
    "AskDepth01" numeric(28,8) NOT NULL,
    "BidDepth05" numeric(28,8) NOT NULL,     -- -0.5%
    "AskDepth05" numeric(28,8) NOT NULL,
    "BidDepth10" numeric(28,8) NOT NULL,     -- -1.0%
    "AskDepth10" numeric(28,8) NOT NULL,

    -- Стенки: крупнейшая одиночная заявка и её удалённость от mid.
    -- Уровень, который рынок «видит» и на котором часто разворачивается.
    "MaxBidWall"        numeric(28,8) NOT NULL,
    "MaxBidWallDistBps" numeric(12,4) NOT NULL,
    "MaxAskWall"        numeric(28,8) NOT NULL,
    "MaxAskWallDistBps" numeric(12,4) NOT NULL,

    -- Скорость обновления книги за минуту — прокси нервозности рынка
    "UpdateCount" integer NOT NULL DEFAULT 0,

    -- Сколько снимков книги усреднено. Меньше ожидаемого — были разрывы связи
    -- или ресинк, и фичи за эту минуту менее надёжны.
    "SampleCount" integer NOT NULL DEFAULT 0,

    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,

    CONSTRAINT "PK_OrderBook_Features" PRIMARY KEY ("Symbol", "OpenTime")
) PARTITION BY RANGE ("OpenTime");

-- Та же помесячная сетка, что у остальных растущих таблиц: месяц дропается во всех сразу.
CREATE INDEX IF NOT EXISTS "IX_OrderBook_Features_OpenTime"
    ON public."OrderBook_Features" USING btree ("OpenTime");

-- ============================================================================
--  Включаем таблицу в общую партиционную сетку и в ротацию
-- ============================================================================

CREATE OR REPLACE FUNCTION public.sp_ensure_month_partitions(target_time bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    month_start TIMESTAMP;
    month_end   TIMESTAMP;
    suffix      TEXT;
    from_ms     BIGINT;
    to_ms       BIGINT;
    floor_ms    BIGINT;
BEGIN
    month_start := DATE_TRUNC('month', TO_TIMESTAMP(target_time / 1000.0) AT TIME ZONE 'UTC');
    month_end   := month_start + INTERVAL '1 month';
    suffix      := TO_CHAR(month_start, 'YYYY_MM');
    from_ms     := EXTRACT(EPOCH FROM month_start)::BIGINT * 1000;
    to_ms       := EXTRACT(EPOCH FROM month_end)::BIGINT * 1000;

    -- Барьер против повторной закачки удалённого: партицию ниже границы ретенции
    -- создавать нельзя, иначе аудитор/импорт архивов вернут дропнутый месяц обратно.
    floor_ms := public.fn_retention_floor_ms();
    IF floor_ms > 0 AND from_ms < floor_ms THEN
        RAISE NOTICE 'Месяц % ниже границы ретенции (%) — партиции не создаются.',
            suffix, TO_CHAR(TO_TIMESTAMP(floor_ms / 1000.0) AT TIME ZONE 'UTC', 'YYYY-MM');
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Trades_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Trades" FOR VALUES FROM (%s) TO (%s)',
                       'Trades_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Ohlcv_1min_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Ohlcv_1min" FOR VALUES FROM (%s) TO (%s)',
                       'Ohlcv_1min_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'Ohlcv_Features_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."Ohlcv_Features" FOR VALUES FROM (%s) TO (%s)',
                       'Ohlcv_Features_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'OrderBook_Features_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."OrderBook_Features" FOR VALUES FROM (%s) TO (%s)',
                       'OrderBook_Features_' || suffix, from_ms, to_ms);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'DataQualityReports_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."DataQualityReports" FOR VALUES FROM (%L) TO (%L)',
                       'DataQualityReports_' || suffix, month_start::date, month_end::date);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = 'DataQualityFindings_' || suffix AND n.nspname = 'public') THEN
        EXECUTE format('CREATE TABLE public.%I PARTITION OF public."DataQualityFindings" FOR VALUES FROM (%L) TO (%L)',
                       'DataQualityFindings_' || suffix,
                       month_start AT TIME ZONE 'UTC', month_end AT TIME ZONE 'UTC');
    END IF;
END;
$$;

-- Размер: добавляем фичи стакана в подсчёт, на котором основана ротация.
CREATE OR REPLACE FUNCTION public.fn_partitioned_size_bytes() RETURNS bigint
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(SUM(pg_total_relation_size(i.inhrelid)), 0)
    FROM pg_inherits i
    WHERE i.inhparent IN (
        'public."Trades"'::regclass,
        'public."Ohlcv_1min"'::regclass,
        'public."Ohlcv_Features"'::regclass,
        'public."OrderBook_Features"'::regclass,
        'public."DataQualityReports"'::regclass,
        'public."DataQualityFindings"'::regclass
    );
$$;

-- Ротация: месяц дропается и здесь тоже — иначе фичи стакана пережили бы тики,
-- из которых считались свечи того же периода.
CREATE OR REPLACE FUNCTION public.sp_rotate_partitions(
    p_max_bytes bigint,
    p_min_months_to_keep integer DEFAULT 6
) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    current_month TIMESTAMP;
    target_month  TIMESTAMP;
    oldest_suffix TEXT;
    oldest_month  TIMESTAMP;
    keep_before   TIMESTAMP;
    total_bytes   BIGINT;
    new_floor_ms  BIGINT;
    dropped       INT := 0;
BEGIN
    current_month := DATE_TRUNC('month', NOW() AT TIME ZONE 'UTC');
    keep_before   := current_month - (p_min_months_to_keep || ' months')::INTERVAL;

    FOR target_month IN
        SELECT m FROM generate_series(current_month, current_month + INTERVAL '1 month', INTERVAL '1 month') AS m
    LOOP
        PERFORM public.sp_ensure_month_partitions(
            EXTRACT(EPOCH FROM target_month)::BIGINT * 1000);
    END LOOP;

    LOOP
        total_bytes := public.fn_partitioned_size_bytes();
        EXIT WHEN total_bytes <= p_max_bytes;

        SELECT substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        INTO oldest_suffix
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'public."Trades"'::regclass
        ORDER BY substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        LIMIT 1;

        EXIT WHEN oldest_suffix IS NULL;

        oldest_month := TO_DATE(oldest_suffix, 'YYYY_MM');

        IF oldest_month >= keep_before THEN
            RAISE WARNING
                'Размер % байт выше порога %, но самый старый месяц (%) свежее окна в % мес. Ротация остановлена — нужно расширять диск или поднимать порог.',
                total_bytes, p_max_bytes, oldest_suffix, p_min_months_to_keep;
            EXIT;
        END IF;

        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Trades_'              || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Ohlcv_1min_'          || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'Ohlcv_Features_'      || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'OrderBook_Features_'  || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'DataQualityReports_'  || oldest_suffix);
        EXECUTE format('DROP TABLE IF EXISTS public.%I', 'DataQualityFindings_' || oldest_suffix);

        dropped := dropped + 1;
        RAISE NOTICE 'Ротация: месяц % удалён во всех таблицах (было % байт, порог %).',
            oldest_suffix, total_bytes, p_max_bytes;
    END LOOP;

    IF dropped > 0 THEN
        new_floor_ms := public.fn_retention_floor_ms();

        UPDATE public."HistoricalAudit_Watermarks"
        SET "LastChecked_Timestamp" = new_floor_ms,
            "LastChecked_TradeId"   = 0
        WHERE "LastChecked_Timestamp" < new_floor_ms;

        RAISE NOTICE 'Ротация завершена: удалено месяцев — %. Новая граница ретенции: %.',
            dropped, TO_CHAR(TO_TIMESTAMP(new_floor_ms / 1000.0) AT TIME ZONE 'UTC', 'YYYY-MM');
    END IF;
END;
$$;

-- Партиции под уже существующие месяцы.
DO $$
DECLARE
    suffix TEXT;
BEGIN
    FOR suffix IN
        SELECT substring(c.relname FROM 'Trades_(\d{4}_\d{2})')
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'public."Trades"'::regclass
        ORDER BY 1
    LOOP
        PERFORM public.sp_ensure_month_partitions(
            EXTRACT(EPOCH FROM TO_DATE(suffix, 'YYYY_MM'))::BIGINT * 1000);
    END LOOP;
END $$;

COMMIT;
