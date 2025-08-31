-- Функция для получения статистики по качеству данных (статусам блоков аудита)
-- для указанного символа и временного диапазона.
CREATE OR REPLACE FUNCTION public.sp_get_data_quality_stats(
    p_symbol TEXT,
    p_start_date DATE,
    p_end_date DATE
)
-- Возвращает таблицу с двумя колонками: статус и количество блоков
RETURNS TABLE("Status" VARCHAR(20), "BlockCount" BIGINT) AS $$
BEGIN
    RETURN QUERY
    SELECT
        b."Status",
        COUNT(b."BlockStartDate") AS "BlockCount"
    FROM public."Audit_Blocks" b
    WHERE
        b."Symbol" = p_symbol AND
        b."BlockStartDate" >= p_start_date AND
        b."BlockStartDate" <= p_end_date
    GROUP BY
        b."Status";
END;
$$ LANGUAGE plpgsql;


-- Пример использования
-- SELECT * FROM public.sp_get_data_quality_stats('BTCUSDT', '2024-01-01', '2024-12-31');

-- Анализ этого результата:
-- Completed: 120: Отлично, 120 блоков (почти весь год) проверены и целостны.
-- Abandoned: 2: Внимание! В вашем наборе данных есть два 3-дневных блока (итого 6 дней), которые содержат неполные данные, и система отказалась от попыток их исправить.
-- Pending: 1: Один блок еще не был проверен.

-- Имея эту информацию, вы можете принять осознанное решение:
-- Обучать модель на этих данных, зная об их неполноценности.
-- Исключить "сбойные" диапазоны из выборки.
-- Попытаться вручную "вылечить" Abandoned блоки, прежде чем начинать обучение.Результат может быть таким:
-- Status	BlockCount
-- Completed	120
-- Abandoned	2
-- Pending	1
