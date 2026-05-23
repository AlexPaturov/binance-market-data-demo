# Документация: 03 - База данных (PostgreSQL)

Этот документ описывает фактическую структуру баз данных проекта `BinanceDataCollector`. Источник истины — `sqlScripts/prod_schema_2026-05-09.sql`.

## 1. Концепция и архитектура данных

База данных спроектирована по принципу **«от сырых данных к агрегированным»**. Это позволяет хранить как максимально детализированную «сырую» информацию (тиковые сделки), так и предварительно обработанные, готовые для быстрого анализа данные (минутные свечи и индикаторы).

**Поток данных внутри базы:**

1. **Поступление:** Тиковые сделки непрерывно поступают в `"Trades"` с `ProcessingStatus = 'new'`. Это «входная воронка» системы.
2. **Агрегация:** Функция `sp_aggregate_trades_to_ohlcv` инкрементально читает новые тики и складывает их в минутные свечи `"Ohlcv_1min"` (тоже с `ProcessingStatus = 'new'`).
3. **Расчёт индикаторов:** Над «свежими» свечами работает feature-pipeline (`sp_process_features` + `sp_upsert_ohlcv_features`), результат сохраняется в `"Ohlcv_Features"`.
4. **Управление подпиской:** Список собираемых пар динамически управляется через `"TrackedSymbols"`.
5. **Аудит целостности:** Состояние исторической дозагрузки фиксируется в `"HistoricalAudit_Watermarks"`.

Для инкрементальной обработки потоков применяется **watermark-механика**: колонки `ProcessingStatus` в исходных таблицах + позиции процессов в `"Processing_Watermarks"`. Подробности — в разделе 5.

Проект использует **две физические базы**: `market_analytics` (бизнес-данные) и `market_analytics_jobs` (Hangfire). См. раздел 2.

---

## 2. Две базы данных

### 2.1. `market_analytics` — основная

Содержит все бизнес-данные: тики, свечи, индикаторы, watermark'и, состояние аудита. Все таблицы — в схеме `public`.

### 2.2. `market_analytics_jobs` — Hangfire

Изолированная служебная БД для Hangfire. Внутри — единственная пользовательская схема `hangfire`, все таблицы создаются Hangfire'ом автоматически при старте Worker'а (`PrepareSchemaIfNecessary = true`). Подробности по серверам и очередям — в разделе 7.

**Зачем разделение:**

- **Изоляция нагрузки:** очереди Hangfire (множество мелких UPDATE'ов на `job`/`state`/`counter`) не делят буферы и locks с тяжёлыми bulk-вставками тиков.
- **Независимое бэкапирование:** бизнес-БД и БД джобов имеют разный жизненный цикл — историю сделок надо хранить долго, состояние Hangfire можно периодически чистить.

---

## 3. Таблицы `market_analytics`

### 3.1. `public."Trades"`

Сырые тиковые сделки с Binance.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"TradeId"`** | `BIGINT` | **(Часть PK)** Уникальный ID сделки от Binance. |
| **`"Symbol"`** | `VARCHAR(20)` | **(Часть PK)** Валютная пара (например, `'BTCUSDT'`). |
| `"Price"` | `NUMERIC(18,8)` | Цена сделки. |
| `"Quantity"` | `NUMERIC(28,8)` | Объём в базовой валюте. |
| `"QuoteQuantity"` | `NUMERIC(28,8)` | Объём в котируемой валюте. |
| `"TradeTime"` | `BIGINT` | Unix-время сделки в миллисекундах (UTC). |
| `"IsBuyerMaker"` | `BOOLEAN` | `true`, если покупатель был мейкером. |
| `"IsBestMatch"` | `BOOLEAN` | `true`, если сделка прошла по лучшей цене. |
| `"OrderId"` | `BIGINT NULL` | ID ордера. |
| `"Commission"` | `NUMERIC(18,8) NULL` | Комиссия (для своих сделок). |
| `"CommissionAsset"` | `VARCHAR(10) NULL` | Валюта комиссии. |
| `"IsMyTrade"` | `BOOLEAN DEFAULT false` | `true`, если это личная сделка. |
| `"ProcessingStatus"` | `VARCHAR(10) DEFAULT 'new'` | Маркер watermark-обработки: `'new'` → `'processed'`. Переключается процедурой `sp_aggregate_trades_to_ohlcv`. |

### 3.2. `public."TrackedSymbols"`

Управляет тем, какие пары система собирает в реальном времени.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(PK)** Валютная пара. |
| `"IsActive"` | `BOOLEAN DEFAULT true` | `true`, если сборщик должен работать с этой парой. |
| `"DateAdded"` | `TIMESTAMPTZ DEFAULT now()` | Дата первого добавления. |
| `"LastScanned"` | `TIMESTAMPTZ NULL` | Когда сканер последний раз видел пару в ТОПе. |

### 3.3. `public."Ohlcv_1min"`

Минутные OHLCV-свечи, агрегированные из `"Trades"`.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(Часть PK)** Валютная пара. |
| **`"OpenTime"`** | `BIGINT` | **(Часть PK)** Unix-время начала минуты в мс (кратно 60000). |
| `"OpenPrice"` | `NUMERIC(18,8)` | Цена первой сделки в минуте. |
| `"HighPrice"` | `NUMERIC(18,8)` | Максимум за минуту. |
| `"LowPrice"` | `NUMERIC(18,8)` | Минимум за минуту. |
| `"ClosePrice"` | `NUMERIC(18,8)` | Цена последней сделки в минуте. |
| `"Volume"` | `NUMERIC(28,8)` | Суммарный объём за минуту. |
| `"ProcessingStatus"` | `VARCHAR(10) DEFAULT 'new'` | Маркер для feature-pipeline: `'new'` → `'processed'`. |

### 3.4. `public."Ohlcv_Features"`

Рассчитанные технические индикаторы по свечам. Связан с `Ohlcv_1min` через составной ключ `(Symbol, OpenTime)`, FK не объявлен.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(Часть PK)** Валютная пара. |
| **`"OpenTime"`** | `BIGINT` | **(Часть PK)** Unix-время начала минуты (тот же ключ, что в `Ohlcv_1min`). |
| `"RSI_14"` | `NUMERIC(10,4) NULL` | Relative Strength Index, период 14. |
| `"MACD_Signal"` | `NUMERIC(18,8) NULL` | Сигнальная линия MACD. |
| `"MACD_Hist"` | `NUMERIC(18,8) NULL` | Гистограмма MACD. |
| `"MA_1051200"` | `NUMERIC(18,8) NULL` | Скользящая средняя (длинное окно). |
| `"MA_201600"` | `NUMERIC(18,8) NULL` | Скользящая средняя (среднее окно). |
| `"CVD"` | `NUMERIC(28,8) NULL` | Cumulative Volume Delta. |

### 3.5. `public."Processing_Watermarks"`

Watermark'и для streaming-процессов. По одной записи на каждый процесс-обработчик (`OhlcvAggregator`, `FeatureCalculator`).

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"ProcessName"`** | `VARCHAR(50)` | **(PK)** Имя процесса-обработчика. |
| `"LastProcessedTimestamp"` | `BIGINT` | Последняя обработанная позиция (Unix-мс или `OpenTime`). |
| `"Status"` | `VARCHAR(20)` | Текущий статус процесса. |
| `"LastUpdate_UTC"` | `TIMESTAMPTZ` | Время последнего обновления watermark'а. |

### 3.6. `public."HistoricalAudit_Watermarks"`

Состояние процесса исторической дозагрузки тиков по символу — где остановилась проверка.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(PK)** Валютная пара. |
| `"LastChecked_TradeId"` | `BIGINT` | TradeId последней проверенной сделки. |
| `"LastChecked_Timestamp"` | `BIGINT` | Время последней проверенной сделки (Unix-мс). |
| `"Status"` | `VARCHAR(20)` | Текущий статус процесса. |
| `"RetryCount"` | `INT DEFAULT 0` | Количество повторных попыток после ошибок. |
| `"LastAttempt_UTC"` | `TIMESTAMPTZ NULL` | Время последней попытки. |

---

## 4. Хранимые процедуры / функции

### 4.1. Сбор и управление символами

#### `public.sp_bulk_insert_trades(...)`
- **Задача:** Максимально быстро вставить пачку тиков в `"Trades"`.
- **Логика:** Принимает массивы (`UNNEST`), делает `INSERT ... ON CONFLICT ("TradeId", "Symbol") DO NOTHING`.
- **Взаимодействие:** [Пишет] → `"Trades"`.

#### `public.sp_update_tracked_symbols(p_symbols VARCHAR[])`
- **Задача:** Атомарно обновить список активных пар.
- **Логика:** Деактивирует пары, которых нет в новом списке (`IsActive = FALSE`), затем `INSERT ... ON CONFLICT DO UPDATE` с `IsActive = TRUE` и обновлением `LastScanned`.
- **Взаимодействие:** [Читает и Пишет] → `"TrackedSymbols"`.

### 4.2. Агрегация

#### `public.sp_aggregate_trades_to_ohlcv()` — watermark-версия (без параметров)
- **Задача:** Инкрементальная агрегация тиков в минутные свечи с автоматическим определением окна по watermark.
- **Логика:**
  1. Читает `LastProcessedTimestamp` процесса `OhlcvAggregator` из `"Processing_Watermarks"`.
  2. Находит `MAX(TradeTime)` среди новых тиков от watermark'а — это конец окна.
  3. Агрегирует тики в temp-таблицу `NewCandles` (Open/Close через `MIN/MAX TradeId`, High/Low/Volume через агрегаты).
  4. `INSERT ... ON CONFLICT DO UPDATE` в `"Ohlcv_1min"` (High/Low мерджатся, Close заменяется).
  5. Помечает тики в окне как `'processed'`, сдвигает watermark.
- **Взаимодействие:** [Читает + Пишет] → `"Trades"`, `"Ohlcv_1min"`, `"Processing_Watermarks"`.

#### `public.sp_aggregate_trades_to_ohlcv(p_start_timestamp BIGINT, p_end_timestamp BIGINT)` — оконная версия
- **Задача:** Агрегация тиков в явно заданном временном окне `[p_start, p_end)`.
- **Логика:** Не использует watermark. Агрегирует через CTE (`array_agg` с сортировкой для корректных Open/Close), делает `INSERT ... ON CONFLICT DO UPDATE` с суммированием Volume. Помечает тики как `'processed'`.
- **Взаимодействие:** [Читает + Пишет] → `"Trades"`, `"Ohlcv_1min"`.

### 4.3. Расчёт features

#### `public.sp_process_features()`
- **Задача:** Завершить обработку «окна» свечей — пометить как `'processed'` и сдвинуть watermark.
- **Логика:**
  1. Читает `LastProcessedTimestamp` процесса `FeatureCalculator`.
  2. Находит `MAX(OpenTime)` среди свечей с `ProcessingStatus = 'new'` от watermark'а.
  3. Помечает все свечи в найденном окне как `'processed'`.
  4. Сдвигает watermark.
- **Взаимодействие:** [Пишет] → `"Ohlcv_1min"`, `"Processing_Watermarks"`.

#### `public.sp_upsert_ohlcv_features(...)`
- **Задача:** Bulk-upsert рассчитанных индикаторов.
- **Логика:** `UNNEST` входных массивов в temp-таблицу, затем `INSERT ... ON CONFLICT ("Symbol", "OpenTime") DO UPDATE`.
- **Взаимодействие:** [Пишет] → `"Ohlcv_Features"`.

### 4.4. Аудит и анализ целостности

#### `public.sp_find_trade_gaps(p_symbol TEXT, p_min_gap_seconds INT)`
- **Задача:** Найти разрывы во времени между соседними сделками по всей истории символа.
- **Логика:** `LAG()` по `TradeTime`, фильтр по разности > порога. Дополнительно проверяет «дыру в конце» (между `MAX(TradeTime)` и текущим временем UTC).
- **Взаимодействие:** [Читает] → `"Trades"`.

#### `public.sp_find_gaps_in_window(p_symbol TEXT, p_start_time_ms BIGINT, p_end_time_ms BIGINT, p_min_gap_seconds INT)`
- **Задача:** Найти разрывы во времени в заданном временном окне.
- **Логика:** Берёт сделки внутри окна + одну сделку до окна (для корректного определения первой дыры), считает `LAG()`.
- **Взаимодействие:** [Читает] → `"Trades"`.

#### `public.sp_find_trade_id_gaps_in_window(p_symbol TEXT, p_start_trade_id BIGINT, p_end_trade_id BIGINT)`
- **Задача:** Найти пропуски в последовательности `TradeId` в заданном диапазоне ID.
- **Логика:** `LAG(TradeId)`, фильтр `TradeId > PrevTradeId + 1` — ищет «дыры» по ID, не по времени.
- **Взаимодействие:** [Читает] → `"Trades"`.

---

## 5. Watermark-механика

Архитектурный паттерн, на котором держится вся streaming-обработка в проекте.

**Зачем.** Тиков и свечей очень много — полное сканирование при каждом запуске процесса было бы катастрофически дорого. Watermark позволяет процессу-обработчику обрабатывать **только новые данные**, появившиеся с момента предыдущего запуска.

**Как работает.**

1. В исходных таблицах (`"Trades"`, `"Ohlcv_1min"`) есть колонка `ProcessingStatus` с дефолтом `'new'`. Все вставленные приложением записи изначально считаются «не обработанными».
2. Таблица `"Processing_Watermarks"` хранит позицию `LastProcessedTimestamp` для каждого процесса (по PK = `ProcessName`).
3. При запуске процесс читает свой watermark, берёт из исходной таблицы записи с `ProcessingStatus = 'new'` и временем `>= watermark`, обрабатывает их, помечает как `'processed'` и сдвигает watermark.

**Где применяется.**

- **Агрегация тиков → свечей:** `sp_aggregate_trades_to_ohlcv()` — процесс `OhlcvAggregator`, исходник `"Trades"`, приёмник `"Ohlcv_1min"`.
- **Feature-pipeline:** C# читает свечи с `ProcessingStatus = 'new'`, вычисляет индикаторы, затем `sp_upsert_ohlcv_features` сохраняет результат и `sp_process_features` помечает свечи как `'processed'` и сдвигает watermark процесса `FeatureCalculator`.

**Преимущества.**

- **Идемпотентность:** повторный запуск на тех же данных не создаст дубликатов (при `ON CONFLICT DO UPDATE` итог тот же).
- **Наблюдаемость:** позиция watermark = простой и точный показатель прогресса по каждому процессу.

---

## 6. Индексы

| Индекс | Таблица | Колонки | Зачем |
| :--- | :--- | :--- | :--- |
| `IX_Trades_Symbol_TradeTime` | `Trades` | `(Symbol, TradeTime DESC)` | Основной для выборки сделок по паре за период (агрегация, аудит). |
| `IX_Trades_ProcessingStatus_TradeTime` | `Trades` | `(ProcessingStatus, TradeTime)` | Watermark-обработка: быстрый поиск `ProcessingStatus = 'new'`. |
| `ix_trades_tradetime_desc` | `Trades` | `(TradeTime DESC)` | Глобальная сортировка по времени без фильтра по символу. |
| `IX_TrackedSymbols_IsActive` | `TrackedSymbols` | `(IsActive)` | Быстрая выборка активных пар. |
| `IX_Ohlcv_1min_ProcessingStatus_OpenTime` | `Ohlcv_1min` | `(ProcessingStatus, OpenTime)` | Feature-pipeline: поиск свечей с `ProcessingStatus = 'new'`. |
| `IX_HistoricalAudit_Watermarks_Status` | `HistoricalAudit_Watermarks` | `(Status)` | Выборка символов в нужном статусе аудита. |

---

## 7. База `market_analytics_jobs` (Hangfire)

- **Версии:** `Hangfire.Core 1.8.21`, `Hangfire.AspNetCore 1.8.21`, `Hangfire.PostgreSql 1.20.12` (target framework `net8.0`).
- **Подключают оба приложения:** Worker и DataManager (`UsePostgreSqlStorage` с `HangfireConnection` connection string).
- **Схема:** `hangfire`. Таблицы (`job`, `jobqueue`, `state`, `server`, `list`, `hash`, `set`, `counter`, `aggregatedcounter`) создаются автоматически при старте Worker'а — он сконфигурирован с `PrepareSchemaIfNecessary = true` и `SchemaName = "hangfire"`.

### 7.1. Серверы Hangfire (только Worker)

В Worker'е сконфигурированы **два сервера**, слушающих непересекающиеся очереди:

| Сервер | Очереди | Назначение |
| :--- | :--- | :--- |
| `PriorityServer` | `realtime`, `quick_audit` | Быстрые приоритетные задачи. |
| `BackgroundServer` | `historical_audit`, `archive_import`, `default` | Тяжёлые фоновые задачи. |

`WorkerCount` зависит от `Environment.ProcessorCount` и режима (Debug / Development → больше воркеров).

В DataManager серверы Hangfire **не запускаются** — приложение только публикует задачи и показывает дашборд.

### 7.2. Dashboard

- **Worker:** `/hangfire`, доступ через `HangfireAuthorizationFilter`.
- **DataManager:** `/hangfire`, использует `AllowAllConnectionsFilter` (в проде защищается на уровне Traefik / Cloudflare Access).

---

## 8. Связи между таблицами

В схеме **нет ни одного `FOREIGN KEY`**. Все связи — логические, через совпадение полей:

- `Trades.Symbol` ↔ `TrackedSymbols.Symbol`
- `Ohlcv_1min(Symbol, OpenTime)` ↔ `Ohlcv_Features(Symbol, OpenTime)`
- `HistoricalAudit_Watermarks.Symbol` ↔ `TrackedSymbols.Symbol`
- `Processing_Watermarks.ProcessName` — имена процессов жёстко прописаны в коде (`OhlcvAggregator`, `FeatureCalculator`).

**Почему так.** Целевая нагрузка — bulk-вставки тиков (десятки тысяч в секунду). FK-проверки на каждой вставке неприемлемы. Целостность поддерживается на уровне приложения: воркеры не пишут в таблицы для пар, которых нет в `TrackedSymbols`, а sp-процедуры используют `ON CONFLICT DO NOTHING` / `DO UPDATE` для устойчивости к гонкам.

---

## 9. Auth-схема

Документация по подсистеме аутентификации и авторизации — в [`docs/common/auth/`](./auth/). Описанная там модель данных — это **спецификация**. В текущей схеме БД таблиц авторизации нет.

В `BinanceDataCollector.DataManager` работает только аутентификация через **Azure AD B2C** (OIDC + Cookie auth). Собственной auth-схемы в БД на текущий момент нет.

---

---

# Скрипт схемы

> `sqlScripts/prod_schema_2026-05-09.sql` — `pg_dump --schema-only` с боевого сервера, PostgreSQL 16.11.

```sql
--
-- PostgreSQL database dump
--
-- Dumped from database version 16.11
-- Dumped by pg_dump version 16.11

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

CREATE SCHEMA public;
COMMENT ON SCHEMA public IS 'standard public schema';


-- ============================================================
-- TABLES
-- ============================================================

CREATE TABLE public."HistoricalAudit_Watermarks" (
    "Symbol"                character varying(20)        NOT NULL,
    "LastChecked_TradeId"   bigint                       NOT NULL,
    "LastChecked_Timestamp" bigint                       NOT NULL,
    "Status"                character varying(20)        NOT NULL,
    "RetryCount"            integer          DEFAULT 0   NOT NULL,
    "LastAttempt_UTC"       timestamp with time zone
);

CREATE TABLE public."Ohlcv_1min" (
    "Symbol"          character varying(20)                       NOT NULL,
    "OpenTime"        bigint                                      NOT NULL,
    "OpenPrice"       numeric(18,8)                               NOT NULL,
    "HighPrice"       numeric(18,8)                               NOT NULL,
    "LowPrice"        numeric(18,8)                               NOT NULL,
    "ClosePrice"      numeric(18,8)                               NOT NULL,
    "Volume"          numeric(28,8)                               NOT NULL,
    "ProcessingStatus" character varying(10) DEFAULT 'new'        NOT NULL
);

CREATE TABLE public."Ohlcv_Features" (
    "Symbol"      character varying(20) NOT NULL,
    "OpenTime"    bigint                NOT NULL,
    "RSI_14"      numeric(10,4),
    "MACD_Signal" numeric(18,8),
    "MACD_Hist"   numeric(18,8),
    "MA_1051200"  numeric(18,8),
    "MA_201600"   numeric(18,8),
    "CVD"         numeric(28,8)
);

CREATE TABLE public."Processing_Watermarks" (
    "ProcessName"            character varying(50)     NOT NULL,
    "LastProcessedTimestamp" bigint                    NOT NULL,
    "Status"                 character varying(20)     NOT NULL,
    "LastUpdate_UTC"         timestamp with time zone  NOT NULL
);

CREATE TABLE public."TrackedSymbols" (
    "Symbol"      character varying(20)                    NOT NULL,
    "IsActive"    boolean          DEFAULT true            NOT NULL,
    "DateAdded"   timestamp with time zone DEFAULT now()   NOT NULL,
    "LastScanned" timestamp with time zone
);

CREATE TABLE public."Trades" (
    "TradeId"         bigint                                      NOT NULL,
    "Symbol"          character varying(20)                       NOT NULL,
    "Price"           numeric(18,8)                               NOT NULL,
    "Quantity"        numeric(28,8)                               NOT NULL,
    "QuoteQuantity"   numeric(28,8)                               NOT NULL,
    "TradeTime"       bigint                                      NOT NULL,
    "IsBuyerMaker"    boolean                                     NOT NULL,
    "IsBestMatch"     boolean                                     NOT NULL,
    "OrderId"         bigint,
    "Commission"      numeric(18,8),
    "CommissionAsset" character varying(10),
    "IsMyTrade"       boolean          DEFAULT false,
    "ProcessingStatus" character varying(10) DEFAULT 'new'        NOT NULL
);


-- ============================================================
-- INDEXES
-- ============================================================

CREATE INDEX "IX_HistoricalAudit_Watermarks_Status"
    ON public."HistoricalAudit_Watermarks" USING btree ("Status");

CREATE INDEX "IX_Ohlcv_1min_ProcessingStatus_OpenTime"
    ON public."Ohlcv_1min" USING btree ("ProcessingStatus", "OpenTime");

CREATE INDEX "IX_TrackedSymbols_IsActive"
    ON public."TrackedSymbols" USING btree ("IsActive");

CREATE INDEX "IX_Trades_ProcessingStatus_TradeTime"
    ON public."Trades" USING btree ("ProcessingStatus", "TradeTime");

CREATE INDEX "IX_Trades_Symbol_TradeTime"
    ON public."Trades" USING btree ("Symbol", "TradeTime" DESC);

CREATE INDEX ix_trades_tradetime_desc
    ON public."Trades" USING btree ("TradeTime" DESC);


-- ============================================================
-- PRIMARY KEYS
-- ============================================================

ALTER TABLE ONLY public."HistoricalAudit_Watermarks"
    ADD CONSTRAINT "HistoricalAudit_Watermarks_pkey" PRIMARY KEY ("Symbol");

ALTER TABLE ONLY public."Ohlcv_Features"
    ADD CONSTRAINT "Ohlcv_Features_pkey" PRIMARY KEY ("Symbol", "OpenTime");

ALTER TABLE ONLY public."Ohlcv_1min"
    ADD CONSTRAINT "PK_Ohlcv_1min" PRIMARY KEY ("Symbol", "OpenTime");

ALTER TABLE ONLY public."Trades"
    ADD CONSTRAINT "PK_Trades" PRIMARY KEY ("TradeId", "Symbol");

ALTER TABLE ONLY public."Processing_Watermarks"
    ADD CONSTRAINT "Processing_Watermarks_pkey" PRIMARY KEY ("ProcessName");

ALTER TABLE ONLY public."TrackedSymbols"
    ADD CONSTRAINT "TrackedSymbols_pkey" PRIMARY KEY ("Symbol");


-- ============================================================
-- FUNCTIONS
-- ============================================================

CREATE FUNCTION public.sp_aggregate_trades_to_ohlcv() RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
    interval BIGINT := 60000;
BEGIN
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'OhlcvAggregator';

    SELECT MAX("TradeTime") INTO end_timestamp
    FROM public."Trades"
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    CREATE TEMP TABLE NewCandles ON COMMIT DROP AS
    WITH Aggregates AS (
        SELECT "Symbol", ("TradeTime" / interval) * interval AS "OpenTime", MIN("Price") AS "LowPrice", MAX("Price") AS "HighPrice",
               SUM("Quantity") AS "Volume", MIN("TradeId") AS "FirstTradeId", MAX("TradeId") AS "LastTradeId"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp AND "TradeTime" <= end_timestamp
        GROUP BY 1, 2
    )
    SELECT agg."Symbol", agg."OpenTime", f."Price" AS "OpenPrice", agg."HighPrice", agg."LowPrice", l."Price" AS "ClosePrice", agg."Volume"
    FROM Aggregates agg
    JOIN public."Trades" f ON agg."FirstTradeId" = f."TradeId"
    JOIN public."Trades" l ON agg."LastTradeId" = l."TradeId";

    IF NOT FOUND THEN RETURN; END IF;

    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume")
    SELECT "Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume" FROM NewCandles
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE
    SET "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = EXCLUDED."Volume";

    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new' AND "TradeTime" >= start_timestamp AND "TradeTime" <= end_timestamp;

    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'OhlcvAggregator';
END;
$$;


CREATE FUNCTION public.sp_aggregate_trades_to_ohlcv(p_start_timestamp bigint, p_end_timestamp bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    interval_ms BIGINT := 60000;
BEGIN
    WITH TradesInWindow AS (
        SELECT "Symbol", "TradeId", "Price", "Quantity", "TradeTime"
        FROM public."Trades"
        WHERE "ProcessingStatus" = 'new'
          AND "TradeTime" >= p_start_timestamp
          AND "TradeTime" < p_end_timestamp
    ),
    MinuteAggregates AS (
        SELECT
            "Symbol",
            ("TradeTime" / interval_ms) * interval_ms AS "OpenTime",
            MIN("Price") AS "Low",
            MAX("Price") AS "High",
            SUM("Quantity") AS "Vol",
            (array_agg("Price" ORDER BY "TradeTime" ASC, "TradeId" ASC))[1] AS "Open",
            (array_agg("Price" ORDER BY "TradeTime" DESC, "TradeId" DESC))[1] AS "Close"
        FROM TradesInWindow
        GROUP BY "Symbol", "OpenTime"
    )
    INSERT INTO public."Ohlcv_1min" ("Symbol", "OpenTime", "OpenPrice", "HighPrice", "LowPrice", "ClosePrice", "Volume", "ProcessingStatus")
    SELECT "Symbol", "OpenTime", "Open", "High", "Low", "Close", "Vol", 'new'
    FROM MinuteAggregates
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "HighPrice" = GREATEST(public."Ohlcv_1min"."HighPrice", EXCLUDED."HighPrice"),
        "LowPrice" = LEAST(public."Ohlcv_1min"."LowPrice", EXCLUDED."LowPrice"),
        "ClosePrice" = EXCLUDED."ClosePrice",
        "Volume" = public."Ohlcv_1min"."Volume" + EXCLUDED."Volume",
        "ProcessingStatus" = 'new';

    UPDATE public."Trades"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new'
      AND "TradeTime" >= p_start_timestamp
      AND "TradeTime" < p_end_timestamp;
END;
$$;


CREATE FUNCTION public.sp_bulk_insert_trades(p_trade_ids bigint[], p_symbols character varying[], p_prices numeric[], p_quantities numeric[], p_quote_quantities numeric[], p_trade_times bigint[], p_is_buyer_makers boolean[], p_is_best_matches boolean[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO public."Trades" ("TradeId", "Symbol", "Price", "Quantity", "QuoteQuantity", "TradeTime", "IsBuyerMaker", "IsBestMatch")
    SELECT * FROM UNNEST(p_trade_ids, p_symbols, p_prices, p_quantities, p_quote_quantities, p_trade_times, p_is_buyer_makers, p_is_best_matches)
    ON CONFLICT ("TradeId", "Symbol") DO NOTHING;
END;
$$;


CREATE FUNCTION public.sp_find_gaps_in_window(p_symbol text, p_start_time_ms bigint, p_end_time_ms bigint, p_min_gap_seconds integer) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH WindowTrades AS (
        SELECT * FROM (
            SELECT "TradeId", "TradeTime"
            FROM public."Trades"
            WHERE "Symbol" = p_symbol AND "TradeTime" < p_start_time_ms
            ORDER BY "TradeTime" DESC
            LIMIT 1
        ) AS before_window
        UNION ALL
        SELECT "TradeId", "TradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
          AND "TradeTime" >= p_start_time_ms
          AND "TradeTime" <= p_end_time_ms
    ),
    OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM WindowTrades
    )
    SELECT "PrevTradeTime" AS "GapStart", "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE "PrevTradeTime" IS NOT NULL
      AND ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000);
END;
$$;


CREATE FUNCTION public.sp_find_trade_gaps(p_symbol text, p_min_gap_seconds integer) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT
            "TradeTime",
            LAG("TradeTime", 1) OVER (ORDER BY "TradeTime" ASC, "TradeId" ASC) AS "PrevTradeTime"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol
    )
    SELECT "PrevTradeTime" AS "GapStart", "TradeTime" AS "GapEnd"
    FROM OrderedTrades
    WHERE ("TradeTime" - "PrevTradeTime") > (p_min_gap_seconds * 1000)

    UNION ALL

    SELECT
        MAX(t."TradeTime") AS "GapStart",
        (EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT AS "GapEnd"
    FROM public."Trades" t
    WHERE t."Symbol" = p_symbol
    HAVING ((EXTRACT(EPOCH FROM NOW() AT TIME ZONE 'UTC') * 1000)::BIGINT - MAX(t."TradeTime")) > (p_min_gap_seconds * 1000);
END;
$$;


CREATE FUNCTION public.sp_find_trade_id_gaps_in_window(p_symbol text, p_start_trade_id bigint, p_end_trade_id bigint) RETURNS TABLE("GapStart" bigint, "GapEnd" bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    RETURN QUERY
    WITH OrderedTrades AS (
        SELECT "TradeId", LAG("TradeId", 1) OVER (ORDER BY "TradeId") AS "PrevTradeId"
        FROM public."Trades"
        WHERE "Symbol" = p_symbol AND "TradeId" >= p_start_trade_id AND "TradeId" <= p_end_trade_id
    )
    SELECT "PrevTradeId" AS "GapStart", "TradeId" AS "GapEnd"
    FROM OrderedTrades
    WHERE "TradeId" > "PrevTradeId" + 1;
END;
$$;


CREATE FUNCTION public.sp_process_features() RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    start_timestamp BIGINT;
    end_timestamp BIGINT;
BEGIN
    SELECT "LastProcessedTimestamp" INTO start_timestamp
    FROM public."Processing_Watermarks"
    WHERE "ProcessName" = 'FeatureCalculator';

    SELECT MAX("OpenTime") INTO end_timestamp
    FROM public."Ohlcv_1min"
    WHERE "ProcessingStatus" = 'new' AND "OpenTime" >= start_timestamp;

    IF end_timestamp IS NULL THEN RETURN; END IF;

    UPDATE public."Ohlcv_1min"
    SET "ProcessingStatus" = 'processed'
    WHERE "ProcessingStatus" = 'new'
      AND "OpenTime" >= start_timestamp
      AND "OpenTime" <= end_timestamp;

    UPDATE public."Processing_Watermarks"
    SET "LastProcessedTimestamp" = end_timestamp
    WHERE "ProcessName" = 'FeatureCalculator';
END;
$$;


CREATE FUNCTION public.sp_update_tracked_symbols(p_symbols character varying[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE public."TrackedSymbols" SET "IsActive" = FALSE WHERE "IsActive" = TRUE AND "Symbol" <> ALL(p_symbols);
    INSERT INTO public."TrackedSymbols" ("Symbol", "IsActive", "LastScanned")
    SELECT symbol, TRUE, NOW() FROM UNNEST(p_symbols) AS u(symbol)
    ON CONFLICT ("Symbol") DO UPDATE SET "IsActive" = TRUE, "LastScanned" = NOW();
END;
$$;


CREATE FUNCTION public.sp_upsert_ohlcv_features(p_symbols character varying[], p_open_times bigint[], p_rsi_14 numeric[], p_macd_signals numeric[], p_macd_hists numeric[], p_ma_1051200 numeric[], p_ma_201600 numeric[], p_cvds numeric[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    CREATE TEMP TABLE NewFeatures ON COMMIT DROP AS
    SELECT * FROM UNNEST(
        p_symbols, p_open_times, p_rsi_14, p_macd_signals, p_macd_hists,
        p_ma_1051200, p_ma_201600, p_cvds
    ) AS t(
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist",
        "MA_1051200", "MA_201600", "CVD"
    );

    INSERT INTO public."Ohlcv_Features" (
        "Symbol", "OpenTime", "RSI_14", "MACD_Signal", "MACD_Hist",
        "MA_1051200", "MA_201600", "CVD"
    )
    SELECT * FROM NewFeatures
    ON CONFLICT ("Symbol", "OpenTime") DO UPDATE SET
        "RSI_14" = EXCLUDED."RSI_14",
        "MACD_Signal" = EXCLUDED."MACD_Signal",
        "MACD_Hist" = EXCLUDED."MACD_Hist",
        "MA_1051200" = EXCLUDED."MA_1051200",
        "MA_201600" = EXCLUDED."MA_201600",
        "CVD" = EXCLUDED."CVD";
END;
$$;


```
