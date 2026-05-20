# Initial Data Load: 2025-01-01 to Present

Пошаговый план первичной загрузки исторических данных, агрегации и запуска воркеров.

---

## Общая схема пайплайна

```
ZIP скачан → распакован → CSV → Trades → Ohlcv_1min → Ohlcv_Features
                                    ↑                        ↑
                          HistoricalAuditor            FeatureCalculator
                          (закрывает дыры)           (считает индикаторы)
```

---

## Фаза 1 — Символы

**Кто:** разработчик  
**Инструмент:** psql / DBeaver

```sql
SELECT COUNT(*) FROM public."TrackedSymbols" WHERE "IsActive" = true;
```

- Результат `> 0` → переходить к фазе 2
- Результат `= 0` → нажать **"Update Symbols"** на странице Archive, дождаться завершения задачи в Hangfire Dashboard

---

## Фаза 2 — Скачивание архивов

**Кто:** разработчик  
**Инструмент:** страница Archive в UI

**Действия:**
- Дата `01.01.2025` → `сегодня`
- Флаг **Download All**
- Нажать Submit

**Ожидаемый объём:** ~40 символов × ~496 дней ≈ 20 000 ZIP-файлов

**Мониторинг:** Hangfire Dashboard → очередь `archive_import`  
Упавшие задачи (`Failed`) можно перезапустить прямо в Dashboard.

**Критерий завершения:** очередь `archive_import` = 0 enqueued, 0 processing

**Проверка после завершения:**
```sql
-- Количество скачанных файлов по символам (через таблицу, если ведётся учёт)
-- или проверить директорию через интерфейс Archive page
SELECT COUNT(*) FROM public."TrackedSymbols" WHERE "IsActive" = true;
```

---

## Фаза 3 — Распаковка и импорт в Trades

**Кто:** разработчик  
**Инструмент:** страница Archive в UI

**Действия:**
- Выбрать все ZIP-файлы
- Нажать **Process Archives**

**Что происходит внутри:**  
`ArchiveUnpackerWorker` → `CsvImportWorker` (цепочка, автоматически)

> **Важно:** `CsvImportWorker` выполняется строго по одному файлу (`DisableConcurrentExecution`).  
> Это защита от перегрузки БД при bulk insert.  
> При 20 000 файлов выполнение может занять **несколько суток**.

**Мониторинг:** Hangfire Dashboard → `archive_import` queue + вкладка Processing

**Критерий завершения:** очередь `archive_import` пуста, нет активных заданий

**Проверка после завершения:**
```sql
SELECT
    "Symbol",
    COUNT(*)        AS trade_count,
    MIN(to_timestamp("TradeTime" / 1000) AT TIME ZONE 'utc') AS first_trade,
    MAX(to_timestamp("TradeTime" / 1000) AT TIME ZONE 'utc') AS last_trade
FROM public."Trades"
GROUP BY "Symbol"
ORDER BY "Symbol";
```

---

## Фаза 4 — Историческая агрегация (в обход воркера)

**Кто:** разработчик  
**Инструмент:** psql / DBeaver

**Почему не через воркер:**  
`OhlcvAggregatorWorker` обрабатывает одно 15-минутное окно в минуту.  
С 01.01.2025 по сегодня — ~47 000 окон = **≈33 дня** ожидания.

**Решение:** запустить PL/pgSQL-скрипт, который вызывает `sp_aggregate_trades_to_ohlcv` в цикле и сам обновляет вотермарку по завершении.

```sql
DO $$
DECLARE
    v_start    BIGINT := 1735689600000; -- 2025-01-01 00:00:00 UTC
    v_window   BIGINT := 900000;        -- 15 минут в мс
    v_end      BIGINT;
    v_hot_zone BIGINT;
    v_iter     INT := 0;
BEGIN
    -- Граница "горячей зоны" = текущее время - 1 час
    v_hot_zone := (EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'utc') * 1000 - 3600000)::BIGINT;

    WHILE v_start < v_hot_zone LOOP
        v_end := v_start + v_window;
        PERFORM public.sp_aggregate_trades_to_ohlcv(v_start, v_end);
        v_start := v_end;
        v_iter  := v_iter + 1;

        IF v_iter % 500 = 0 THEN
            RAISE NOTICE 'Processed % windows, position: %',
                v_iter,
                to_timestamp(v_start / 1000.0) AT TIME ZONE 'utc';
        END IF;
    END LOOP;

    -- Обновляем вотермарку — воркер продолжит с этой точки
    INSERT INTO public."Processing_Watermarks" ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES ('OhlcvAggregator', v_start - 1, 'Pending', NOW() AT TIME ZONE 'utc')
    ON CONFLICT ("ProcessName") DO UPDATE
        SET "LastProcessedTimestamp" = EXCLUDED."LastProcessedTimestamp",
            "Status"                 = 'Pending',
            "LastUpdate_UTC"         = NOW() AT TIME ZONE 'utc';

    RAISE NOTICE 'Done. Total windows processed: %. Watermark set to: %',
        v_iter,
        to_timestamp(v_start / 1000.0) AT TIME ZONE 'utc';
END $$;
```

**Критерий завершения:** скрипт выводит `Done. Total windows processed: N.`

**Проверка:**
```sql
SELECT * FROM public."Processing_Watermarks" WHERE "ProcessName" = 'OhlcvAggregator';

SELECT COUNT(*) FROM public."Ohlcv_1min";
```

---

## Фаза 5 — Установка вотермарок для HistoricalAuditor

**Кто:** разработчик  
**Инструмент:** psql / DBeaver

Создаёт записи в `HistoricalAudit_Watermarks` для всех активных символов, начиная с 01.01.2025.  
HistoricalAuditor будет верифицировать импортированные данные и при нахождении дыр — ставить задачи на докачку через API.

```sql
-- 1735689599999 = 2025-01-01 00:00:00 UTC - 1ms
INSERT INTO public."HistoricalAudit_Watermarks"
    ("Symbol", "LastChecked_TradeId", "LastChecked_Timestamp", "Status", "RetryCount", "LastAttempt_UTC")
SELECT
    "Symbol",
    0,
    1735689599999,
    'Pending',
    0,
    NOW() AT TIME ZONE 'utc'
FROM public."TrackedSymbols"
WHERE "IsActive" = true
ON CONFLICT ("Symbol") DO UPDATE
    SET "LastChecked_TradeId"   = 0,
        "LastChecked_Timestamp" = 1735689599999,
        "Status"                = 'Pending',
        "RetryCount"            = 0,
        "LastAttempt_UTC"       = NOW() AT TIME ZONE 'utc';

-- Проверка
SELECT COUNT(*), "Status" FROM public."HistoricalAudit_Watermarks" GROUP BY "Status";
```

---

## Фаза 6 — Запуск воркеров

**Кто:** разработчик

1. Убедиться что Worker-приложение запущено
2. Открыть Hangfire Dashboard → вкладка **Recurring Jobs**
3. Убедиться что зарегистрированы:

| Job ID | Воркер | Назначение |
|--------|--------|-----------|
| `ohlcv-aggregator` | OhlcvAggregatorWorker | Агрегация trades → Ohlcv_1min (каждую минуту) |
| `feature-calculator` | FeatureCalculatorWorker | Расчёт индикаторов (каждые 2 мин) |
| `historical-audit` | HistoricalAuditorWorker | Верификация исторических данных (каждые 6 ч) |
| `quick_audit` | QuickAuditorWorker | Закрытие дыр в последних 24 ч (каждые 10 мин) |
| `update-symbols` | SymbolUpdateWorker | Обновление списка символов (раз в день) |

После установки вотермарки (фаза 4) `OhlcvAggregatorWorker` продолжит с точки, где остановился скрипт.  
`FeatureCalculatorWorker` найдёт свечи со статусом `new` и начнёт считать сам.

**Мониторинг:**
```sql
-- Прогресс агрегатора
SELECT "LastProcessedTimestamp",
       to_timestamp("LastProcessedTimestamp" / 1000.0) AT TIME ZONE 'utc' AS watermark_utc,
       "Status"
FROM public."Processing_Watermarks"
WHERE "ProcessName" = 'OhlcvAggregator';

-- Прогресс аудитора по символам
SELECT "Symbol", "Status", "RetryCount",
       to_timestamp("LastChecked_Timestamp" / 1000.0) AT TIME ZONE 'utc' AS checked_up_to
FROM public."HistoricalAudit_Watermarks"
ORDER BY "LastChecked_Timestamp" ASC
LIMIT 20;

-- Рост Ohlcv_Features
SELECT COUNT(*) FROM public."Ohlcv_Features";
```

---

## Точки синхронизации

| Фаза | Сигнал к следующему шагу |
|------|--------------------------|
| 1 | COUNT символов > 0 |
| 2 | Очередь archive_import пуста |
| 3 | Очередь archive_import пуста + SQL-сверка trades |
| 4 | Скрипт вывел `Done. Total windows processed: N` |
| 5 | SQL подтвердил вотермарки для всех символов |
| 6 | Recurring jobs активны в Hangfire Dashboard |
