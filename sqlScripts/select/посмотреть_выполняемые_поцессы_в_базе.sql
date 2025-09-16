SELECT
    pid,
    age(clock_timestamp(), query_start) AS duration,
    usename AS user_name,
    application_name,
    state,
    wait_event_type,
    wait_event,
    query
FROM pg_stat_activity
WHERE state = 'active'
  AND pid <> pg_backend_pid() -- Исключаем наш собственный запрос
ORDER BY duration DESC;