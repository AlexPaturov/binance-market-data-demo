SELECT * FROM "Audit_Blocks"
WHERE
    "Status" = 'Pending' 
    OR 
    ("Status" = 'Failed' AND "LastAttempt" < NOW() - INTERVAL '1 day' AND "RetryCount" < :MaxRetries) -- Повторяем сбойные, если лимит не исчерпан
ORDER BY "BlockStartDate" ASC
LIMIT 10;