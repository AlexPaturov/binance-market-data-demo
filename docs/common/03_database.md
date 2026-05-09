# Документация: 03 - База данных (PostgreSQL)

Этот документ описывает фактическую структуру баз данных проекта `BinanceDataCollector`. Источником истины для DDL служит `tests/BinanceDataCollector.Infrastructure.Tests/schema.sql` (это `pg_dump --schema-only` живой схемы); скрипты в `sqlScripts/` местами устарели — известные расхождения зафиксированы в разделе 10.

## 1. Концепция и архитектура данных

База данных спроектирована по принципу **«от сырых данных к агрегированным»**. Это позволяет хранить как максимально детализированную «сырую» информацию (тиковые сделки), так и предварительно обработанные, готовые для быстрого анализа данные (минутные свечи и индикаторы).

**Поток данных внутри базы:**

1. **Поступление:** Тиковые сделки непрерывно поступают в `"Trades"` с `ProcessingStatus = 'new'`. Это «входная воронка» системы.
2. **Агрегация:** Функция `sp_aggregate_trades_to_ohlcv` инкрементально читает новые тики и складывает их в минутные свечи `"Ohlcv_1min"` (тоже с `ProcessingStatus = 'new'`).
3. **Расчёт индикаторов:** Над «свежими» свечами работает feature-pipeline (`sp_claim_new_ohlcv_for_features` + `sp_process_features` + `sp_upsert_ohlcv_features`), результат сохраняется в `"Ohlcv_Features"`.
4. **Управление подпиской:** Список собираемых пар динамически управляется через `"TrackedSymbols"`.
5. **Аудит целостности:** Состояние исторической дозагрузки фиксируется в `"Audit_Blocks"` и `"HistoricalAudit_Watermarks"`.

Для инкрементальной обработки потоков применяется **watermark-механика**: колонки `ProcessingStatus` в исходных таблицах + позиции процессов в `"Processing_Watermarks"`. Подробности — в разделе 5.

Проект использует **две физические базы**: `market_analytics` (бизнес-данные) и `market_analytics_jobs` (Hangfire). См. раздел 2.

---

## 2. Две базы данных

### 2.1. `market_analytics` — основная

Содержит все бизнес-данные: тики, свечи, индикаторы, состояние аудита, watermark'и. Все таблицы — в схеме `public`. Создание ролей / БД — `sqlScripts/create/create_user.sql` и `sqlScripts/create/init-db.sql`.

### 2.2. `market_analytics_jobs` — Hangfire

Изолированная служебная БД для Hangfire. Создаётся скриптом `sqlScripts/create/init-db.sql` (`CREATE DATABASE market_analytics_jobs`). Внутри — единственная пользовательская схема `hangfire`, все таблицы создаются Hangfire'ом автоматически при старте Worker'а (`PrepareSchemaIfNecessary = true`). Подробности по серверам и очередям — в разделе 7.

**Зачем разделение:**

- **Изоляция нагрузки:** очереди Hangfire (множество мелких UPDATE'ов на `job`/`state`/`counter`) не делят буферы и locks с тяжёлыми bulk-вставками тиков.
- **Независимое бэкапирование:** бизнес-БД и БД джобов имеют разный жизненный цикл — историю сделок надо хранить долго, состояние Hangfire можно периодически чистить.
- **Зеркало dev/prod:** до недавнего времени Hangfire в dev делил БД с бизнес-данными — это создавало расхождение с продом. Сейчас выровнено.

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
| `"IsMyTrade"` | `BOOLEAN DEFAULT false` | `true`, если это личная сделка (задел на будущее). |
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
| `"ProcessingStatus"` | `VARCHAR(10) DEFAULT 'new'` | Маркер для feature-pipeline: `'new'` → `'processing'` → `'processed'`. |

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

### 3.5. `public."Audit_Blocks"`

Управление 3-дневными блоками исторического аудита. Каждая запись = одна задача проверки целостности данных за блок.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(Часть PK)** Валютная пара. |
| **`"BlockStartDate"`** | `DATE` | **(Часть PK)** Начало 3-дневного блока (всегда округлено). |
| `"Status"` | `VARCHAR(20)` | `Pending` / `Completed` / `Failed` / `Abandoned`. |
| `"LastAttempt"` | `TIMESTAMPTZ NULL` | Когда последний раз пытались проверить блок. |
| `"RetryCount"` | `INT DEFAULT 0` | Количество повторных попыток после ошибок. |

### 3.6. `public."Processing_Watermarks"`

Watermark'и для streaming-процессов. По одной записи на каждый процесс-обработчик (`OhlcvAggregator`, `FeatureCalculator`).

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"ProcessName"`** | `VARCHAR(50)` | **(PK)** Имя процесса-обработчика. |
| `"LastProcessedTimestamp"` | `BIGINT` | Последняя обработанная позиция (Unix-мс или `OpenTime`). |

> **Подозрение на расхождение:** в `src/BinanceDataCollector.Worker/docs/miscelanious.md` упоминается версия таблицы с дополнительными колонками `Status` и `LastUpdate_UTC`. В `tests/schema.sql` этих колонок нет. Зафиксировано в разделе 10.

### 3.7. `public."HistoricalAudit_Watermarks"`

Состояние процесса исторической дозагрузки тиков по символу — где остановилась проверка.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(PK)** Валютная пара. |
| `"LastChecked_TradeId"` | `BIGINT` | TradeId последней проверенной сделки. |
| `"LastChecked_Timestamp"` | `BIGINT` | Время последней проверенной сделки (Unix-мс). |
| `"Status"` | `VARCHAR(20)` | Текущий статус процесса. |
| `"RetryCount"` | `INT DEFAULT 0` | Количество ретраев. |
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

#### `public.sp_aggregate_trades_to_ohlcv()` *(watermark-версия)*
- **Задача:** Инкрементальная агрегация тиков в минутные свечи.
- **Логика:**
  1. Читает позицию `LastProcessedTimestamp` процесса `OhlcvAggregator` из `"Processing_Watermarks"`.
  2. Находит «окно» — `Trades` с `ProcessingStatus = 'new'` и `TradeTime >= start`.
  3. Через оконные функции (`first_value` / `last_value`) собирает свечи и делает `INSERT ... ON CONFLICT DO UPDATE` в `"Ohlcv_1min"` (Open остаётся прежним, High/Low пересчитываются, Close — последний).
  4. Помечает обработанные тики как `'processed'`.
  5. Сдвигает watermark.
- **Взаимодействие:** [Читает + Пишет] → `"Trades"`, `"Ohlcv_1min"`, `"Processing_Watermarks"`.

### 4.3. Расчёт features

#### `public.sp_claim_new_ohlcv_for_features(p_batch_size INT)`
- **Задача:** Атомарно «забрать» пачку свежих свечей под расчёт индикаторов так, чтобы конкурирующие воркеры не взяли те же строки.
- **Логика:** `LOCK TABLE Processing_Watermarks IN EXCLUSIVE MODE`, выбирает свечи `Ohlcv_1min` с `ProcessingStatus = 'new'` через `FOR UPDATE SKIP LOCKED`, помечает их как `'processing'`, возвращает строки вызывающему коду.
- **Взаимодействие:** [Читает + Пишет] → `"Ohlcv_1min"`, `"Processing_Watermarks"`.

#### `public.sp_process_features()`
- **Задача:** Завершить обработку «окна» свечей — пометить как `'processed'` и сдвинуть watermark.
- **Логика:** Не считает индикаторы (расчёты — в C#), только обновляет статусы и watermark.
- **Взаимодействие:** [Пишет] → `"Ohlcv_1min"`, `"Processing_Watermarks"`.

#### `public.sp_upsert_ohlcv_features(...)`
- **Задача:** Bulk-upsert рассчитанных индикаторов.
- **Логика:** `UNNEST` входных массивов в temp-таблицу, затем `INSERT ... ON CONFLICT ("Symbol", "OpenTime") DO UPDATE`.
- **Взаимодействие:** [Пишет] → `"Ohlcv_Features"`.

### 4.4. Аудит и анализ целостности

#### `public.sp_find_trade_gaps(p_symbol, p_min_gap_seconds)`
- **Задача:** Найти разрывы во времени между соседними сделками без ограничения по окну.
- **Логика:** `LAG()` по `TradeTime`, фильтр по разности > порога. Дополнительно проверяет «дыру в конце» (между `MAX(TradeTime)` и текущим временем).
- **Взаимодействие:** [Читает] → `"Trades"`.

#### `public.sp_find_gaps_in_window(p_symbol, p_start_time_ms, p_end_time_ms, p_min_gap_seconds)`
- **Задача:** То же, что `sp_find_trade_gaps`, но в заданном временном окне. Используется аудитом в `Audit_Blocks`.
- **Логика:** Берёт сделки внутри окна + одну сделку до окна (для корректного определения первой дыры), считает `LAG()`.
- **Взаимодействие:** [Читает] → `"Trades"`.

#### `public.sp_get_data_quality_stats(p_symbol, p_start_date, p_end_date)`
- **Задача:** Сводка по `"Audit_Blocks"` за период: сколько блоков `Completed` / `Pending` / `Failed` / `Abandoned`.
- **Взаимодействие:** [Читает] → `"Audit_Blocks"`.

---

## 5. Watermark-механика

Архитектурный паттерн, на котором держится вся streaming-обработка в проекте.

**Зачем.** Тиков и свечей очень много — полное сканирование при каждом запуске процесса было бы катастрофически дорого. Watermark позволяет процессу-обработчику обрабатывать **только новые данные**, появившиеся с момента предыдущего запуска.

**Как работает.**

1. В исходных таблицах (`"Trades"`, `"Ohlcv_1min"`) есть колонка `ProcessingStatus` с дефолтом `'new'`. Все вставленные приложением записи изначально считаются «не обработанными».
2. Таблица `"Processing_Watermarks"` хранит позицию `LastProcessedTimestamp` для каждого процесса (по PK = `ProcessName`).
3. При запуске процесс читает свой watermark, берёт из исходной таблицы записи с `ProcessingStatus = 'new'` и временем `>= watermark`, обрабатывает их, помечает как `'processed'` и сдвигает watermark.

**Где применяется.**

- **Агрегация тиков → свечей:** `sp_aggregate_trades_to_ohlcv` — процесс `OhlcvAggregator`, исходник `"Trades"`, приёмник `"Ohlcv_1min"`.
- **Feature-pipeline:** `sp_claim_new_ohlcv_for_features` + `sp_process_features` + `sp_upsert_ohlcv_features` — процесс `FeatureCalculator`, исходник `"Ohlcv_1min"`, приёмник `"Ohlcv_Features"`.

**Преимущества.**

- **Идемпотентность:** повторный запуск на тех же данных не создаст дубликатов (при `ON CONFLICT DO UPDATE` итог тот же).
- **Параллелизм:** `FOR UPDATE SKIP LOCKED` в `sp_claim_new_ohlcv_for_features` позволяет нескольким воркерам брать непересекающиеся пачки.
- **Наблюдаемость:** позиция watermark = простой и точный показатель прогресса по каждому процессу.

---

## 6. Индексы

Главные индексы в `market_analytics` (по `tests/schema.sql`):

| Индекс | Таблица | Колонки | Зачем |
| :--- | :--- | :--- | :--- |
| `IX_Trades_Symbol_TradeTime` | `Trades` | `(Symbol, TradeTime DESC)` | Основной для выборки сделок по паре за период (агрегация, аудит). |
| `IX_Trades_ProcessingStatus_TradeTime` | `Trades` | `(ProcessingStatus, TradeTime)` | Watermark-обработка: быстрый поиск `ProcessingStatus = 'new'`. |
| `idx_trades_symbol_tradeid_tradetime` | `Trades` | `(Symbol, TradeId, TradeTime)` | Покрывающий для фильтра по символу + сортировки по TradeId. |
| `idx_trades_symbol_date_utc` | `Trades` | `(Symbol, date(to_timestamp(TradeTime/1000) AT TIME ZONE 'UTC'))` | Функциональный индекс по календарной дате — для запросов вида «сделки за день». |
| `IX_TrackedSymbols_IsActive` | `TrackedSymbols` | `(IsActive)` | Быстрая выборка активных пар. |
| `IX_Ohlcv_1min_ProcessingStatus_OpenTime` | `Ohlcv_1min` | `(ProcessingStatus, OpenTime)` | Feature-pipeline: поиск `ProcessingStatus = 'new'`. |
| `IX_Audit_Blocks_Status_LastAttempt` | `Audit_Blocks` | `(Status, LastAttempt)` | Поиск блоков, которые пора повторно проверить. |
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
- **DataManager:** `/hangfire`, использует `AllowAllConnectionsFilter` (т.е. открыт; в проде защищается на уровне Traefik / Cloudflare Access).

---

## 8. Связи между таблицами

В схеме **нет ни одного `FOREIGN KEY`**. Все связи — логические, через совпадение полей:

- `Trades.Symbol` ↔ `TrackedSymbols.Symbol`
- `Ohlcv_1min(Symbol, OpenTime)` ↔ `Ohlcv_Features(Symbol, OpenTime)`
- `Audit_Blocks.Symbol` ↔ `TrackedSymbols.Symbol`
- `HistoricalAudit_Watermarks.Symbol` ↔ `TrackedSymbols.Symbol`
- `Processing_Watermarks.ProcessName` — без таблицы-источника, имена процессов жёстко прописаны в коде (`OhlcvAggregator`, `FeatureCalculator`).

**Почему так.** Целевая нагрузка — bulk-вставки тиков (десятки тысяч в секунду). FK-проверки на каждой вставке и блокировки по родительским таблицам неприемлемы. Целостность поддерживается на уровне приложения: воркеры не пишут в таблицы для пар, которых нет в `TrackedSymbols`, а sp-процедуры используют `ON CONFLICT DO NOTHING` / `DO UPDATE` для устойчивости к гонкам.

---

## 9. Auth-схема

Документация по подсистеме аутентификации и авторизации — в [`docs/common/auth/`](./auth/). Описанная там модель данных (`User`, `UserIdentity`, `Role`, `Permission`, `UserRole`, `RolePermission`, `AuthorizationSnapshot`, `SystemState`, `AuditEvent`) — это **спецификация**. Реализация в коде пока не начата: ни одной соответствующей сущности в `src/BinanceDataCollector.Domain/Entities/` и ни одной таблицы в `tests/schema.sql`.

В `BinanceDataCollector.DataManager` работает только аутентификация через **Azure AD B2C** (OIDC + Cookie auth, без серверной модели прав). Собственной auth-схемы в БД на текущий момент нет.

---

## 10. Известные расхождения и TODO

Список долгов перед БД и инфраструктурой DDL — чтобы при возврате к проекту через месяц мы помнили, что не доделано.

- **`sqlScripts/ddl/dbFromScratch.sql` устарел.** Нет колонки `ProcessingStatus` на `Trades` и `Ohlcv_1min`, нет таблиц `Audit_Blocks` (она в отдельном файле), `Processing_Watermarks`, `HistoricalAudit_Watermarks`, нет watermark-индексов и функционального `idx_trades_symbol_date_utc`. Содержит **старую** версию `sp_aggregate_trades_to_ohlcv` (без watermark). Запуск этого скрипта поверх живой БД откатит схему. Требует переработки.
- **DDL для `Processing_Watermarks` и `HistoricalAudit_Watermarks` отсутствует в `sqlScripts/`.** Таблицы существуют на серверах, но восстановить их с нуля по репозиторию невозможно. Источник истины — `tests/schema.sql`.
- **Расхождение по `Processing_Watermarks`:** в `tests/schema.sql` две колонки (`ProcessName`, `LastProcessedTimestamp`); в `src/BinanceDataCollector.Worker/docs/miscelanious.md` упоминается INSERT с четырьмя (`Status`, `LastUpdate_UTC` дополнительно). Какая версия фактически работает на dev/prod — нужно проверить.
- **`sp_find_trade_id_gaps_in_window` вызывается из `HistoricalAuditRepository`, но не определена в `sqlScripts/`.** Функция работает на серверах (раз код успешно её вызывает), но в репозитории её определения нет. Нужно восстановить.
- **Две версии `sp_aggregate_trades_to_ohlcv` в `sqlScripts/`:** старая в `ddl/dbFromScratch.sql` и новая (watermark) в `create/sp/watermark/`. Старую нужно удалить или явно пометить как deprecated, чтобы случайный прогон `dbFromScratch.sql` не уронил пайплайн.
- **`postgres-config/custom.conf` — пустой файл.** Удалить или наполнить осмысленным содержимым.
- **EF Core / миграции отсутствуют.** Схема ведётся вручную через SQL. Это сознательный выбор (raw Dapper-репозитории), но требует дисциплины: любое изменение схемы должно сопровождаться правкой соответствующих файлов в `sqlScripts/` и регенерацией `tests/schema.sql` (через `pg_dump`).
