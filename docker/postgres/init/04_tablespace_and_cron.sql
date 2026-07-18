-- Холодное пространство и планировщик эвакуации. Выполняется на чистой инициализации,
-- в БД market_analytics (в ней же живёт cron — см. cron.database_name в команде bdc_db).

-- Холодный диск для закрытых месяцев Trades. Каталог смонтирован с внешнего HDD и
-- принадлежит postgres. Функция sp_evacuate_next_cold_partition без этого пространства
-- просто бездействует, так что порядок относительно схемы неважен.
CREATE TABLESPACE cold LOCATION '/mnt/pg_tablespaces/cold';

-- pg_cron загружен через shared_preload_libraries (команда bdc_db). Расширение создаётся
-- в той же БД, что указана в cron.database_name.
CREATE EXTENSION IF NOT EXISTS pg_cron;

-- Эвакуация закрытых месяцев на холодный диск — раз в час, силами самой БД.
-- Приложение из этого пути убрано полностью: ни Hangfire-задачи, ни удерживаемого
-- соединения, ни искусственного таймаута. Наложение запусков функция гасит сама
-- (advisory-lock внутри sp_evacuate_next_cold_partition): затянувшаяся копия не
-- порождает второй параллельный переезд.
SELECT cron.schedule(
    'evacuate-cold-partitions',
    '0 * * * *',
    $$SELECT public.sp_evacuate_next_cold_partition()$$
);
