-- Повторяющиеся задачи "дубли"
SELECT
    j."invocationdata",
    COUNT(*) AS "DuplicateCount"
FROM hangfire."jobqueue" jq
JOIN hangfire."job" j ON jq."jobid" = j."id"
GROUP BY
    j."invocationdata"
HAVING
    COUNT(*) > 1
ORDER BY
    "DuplicateCount" DESC
LIMIT 100;