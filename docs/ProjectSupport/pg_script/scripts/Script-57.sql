SELECT * FROM sp_find_gaps_in_window(
    'BTCUSD', 
    EXTRACT(EPOCH FROM '2025-08-01 00:00:00'::timestamp AT TIME ZONE 'UTC')::bigint * 1000,
    EXTRACT(EPOCH FROM '2025-09-06 23:59:59'::timestamp AT TIME ZONE 'UTC')::bigint * 1000,
    500
);