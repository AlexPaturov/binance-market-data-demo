CREATE TABLE IF NOT EXISTS public."Audit_Blocks" (
    "Symbol"          VARCHAR(20) NOT NULL,
    "BlockStartDate"  DATE        NOT NULL, -- Начало 3-дневного блока (всегда округлено)
    "Status"          VARCHAR(20) NOT NULL, -- 'Pending', 'Completed', 'Failed', 'Abandoned' 
    "LastAttempt"     TIMESTAMPTZ,          -- Когда в последний раз пытались проверить
    "RetryCount"      INT         NOT NULL DEFAULT 0,

    CONSTRAINT "PK_Audit_Blocks" PRIMARY KEY ("Symbol", "BlockStartDate")
);

-- Symbol, BlockStartDate: Составной ключ, уникально идентифицирующий "задачу".
-- Status: Главное поле, показывающее состояние.
--   Pending: Нужно проверить.
--   Completed: Проверено, все хорошо.
--   Failed: Была ошибка, нужно попробовать снова.
--   Abandoned: Ошибка повторялась слишком много раз, прекращаем попытки.
-- LastAttempt: Временная метка последней попытки. Нужна, чтобы не "долбить" сбойные блоки слишком часто.
-- RetryCount: Счетчик сбоев.

CREATE INDEX IF NOT EXISTS "IX_Audit_Blocks_Status_LastAttempt" ON public."Audit_Blocks" ("Status", "LastAttempt");