select * from public."Processing_Watermarks";

--drop TABLE public."Processing_Watermarks"

CREATE TABLE public."Processing_Watermarks" (
    "ProcessName" VARCHAR(50) NOT NULL PRIMARY KEY, -- Имя процесса, например 'OhlcvAggregator'
    "LastProcessedTimestamp" BIGINT NOT NULL,       -- Последняя обработанная метка времени (Unix ms)
    "Status" VARCHAR(20) NOT NULL,                  -- 'Pending', 'Completed'
    "LastUpdate_UTC" TIMESTAMPTZ NOT NULL
);

INSERT INTO public."Processing_Watermarks" 
    ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
VALUES
    ('OhlcvAggregator', 0, 'Pending', NOW() AT TIME ZONE 'utc');