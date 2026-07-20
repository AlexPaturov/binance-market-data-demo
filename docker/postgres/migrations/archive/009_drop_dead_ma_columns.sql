-- Фаза 2 из 2: удаление MA-колонок и старой сигнатуры sp_upsert_ohlcv_features.
--
-- Применять ПОСЛЕ деплоя воркера, пишущего фичи 6-параметровым вызовом (фаза 1 — 008):
-- старая 8-параметровая функция нужна старому воркеру до самого рестарта.
--
-- Обоснование удаления — в шапке 008: двухлетней минутной истории, которой требует
-- MA_1051200, при текущем старте истории и ретенции не появится.
--
--   psql -U bindatacoll -d market_analytics -f 009_drop_dead_ma_columns.sql

BEGIN;

DROP FUNCTION IF EXISTS public.sp_upsert_ohlcv_features(
    character varying[], bigint[], numeric[], numeric[], numeric[], numeric[], numeric[], numeric[]);

ALTER TABLE public."Ohlcv_Features" DROP COLUMN IF EXISTS "MA_1051200";
ALTER TABLE public."Ohlcv_Features" DROP COLUMN IF EXISTS "MA_201600";

COMMIT;
