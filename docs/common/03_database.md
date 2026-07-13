# Документация: 03 - База данных (PostgreSQL)

Этот документ описывает фактическую структуру баз данных проекта `BinanceDataCollector`. Источник истины — baseline `docker/postgres/init/02_schema.sql`, который применяется автоматически при инициализации БД.

## 1. Концепция и архитектура данных

База данных спроектирована по принципу **«от сырых данных к агрегированным»**. Это позволяет хранить как максимально детализированную «сырую» информацию (тиковые сделки), так и предварительно обработанные, готовые для быстрого анализа данные (минутные свечи и индикаторы).

**Поток данных внутри базы:**

1. **Поступление:** Тиковые сделки непрерывно поступают в `"Trades"` с `ProcessingStatus = 'new'`. Это «входная воронка» системы.
2. **Агрегация:** `sp_aggregate_new_trades` берёт минуты, в которых есть необработанные тики, и пересчитывает по ним свечи `"Ohlcv_1min"` (тоже со статусом `'new'`).
3. **Расчёт индикаторов:** `FeatureCalculatorWorker` забирает свечи со статусом `'new'`, считает индикаторы и сохраняет их через `sp_upsert_ohlcv_features` в `"Ohlcv_Features"`.
4. **Стакан:** `OrderBookCollectorWorker` держит книгу в памяти и раз в минуту пишет из неё фичи в `"OrderBook_Features"`.
4. **Управление подпиской:** Список собираемых пар динамически управляется через `"TrackedSymbols"`.
5. **Аудит целостности:** Состояние исторической дозагрузки фиксируется в `"HistoricalAudit_Watermarks"`, результаты периодической проверки качества сырых тиков по паре и месяцу — в `"DataQualityReports"`.

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
| `"ProcessingStatus"` | `VARCHAR(10) DEFAULT 'new'` | `'new'` → `'processed'`. Именно по этой колонке (а не по времени) агрегатор находит необработанные тики — поэтому порядок их прихода не важен. Переключается процедурой `sp_aggregate_new_trades`. |

Таблица физически партиционирована (`PARTITION BY RANGE ("TradeTime")`) на помесячные партиции `"Trades_YYYY_MM"`. Та же помесячная сетка — у `Ohlcv_1min`, `Ohlcv_Features` и таблиц качества данных; месяц дропается во всех сразу. См. раздел 4.5.

### 3.2. `public."TrackedSymbols"`

Управляет тем, какие пары система собирает в реальном времени.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(PK)** Валютная пара. |
| `"IsActive"` | `BOOLEAN DEFAULT true` | `true`, если сборщик должен работать с этой парой. |
| `"DateAdded"` | `TIMESTAMPTZ DEFAULT now()` | Дата первого добавления. |
| `"LastScanned"` | `TIMESTAMPTZ NULL` | Когда сканер последний раз видел пару в ТОПе. |
| `"MissedScans"` | `INTEGER DEFAULT 0` | Сколько сканов подряд пара не попадала в ТОП. Сбрасывается в `0` при возвращении. На пороге (`3`) пара деактивируется — см. `sp_update_tracked_symbols`. |

### 3.3. `public."Ohlcv_1min"`

Минутные OHLCV-свечи, агрегированные из `"Trades"`. Партиционирована помесячно по `OpenTime` — той же сеткой, что `Trades` (раздел 4.5).

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

Рассчитанные технические индикаторы по свечам. Связан с `Ohlcv_1min` через составной ключ `(Symbol, OpenTime)`, FK не объявлен. Партиционирована помесячно по `OpenTime` (раздел 4.5).

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

### 3.5. `public."OrderBook_Features"`

Поминутные фичи стакана для ML. **Сырой L2 не хранится**: полная глубина с диффами по 40 парам — это ~190 ГБ/месяц даже в экономной схеме, что съело бы бюджет, отведённый под тики. Книга держится в памяти коллектора, раз в 5 секунд с неё снимается срез, в конце минуты усреднённые числа пишутся одной строкой на пару (~0.4 ГБ/месяц). Партиционирована помесячно по `OpenTime` — той же сеткой, что свечи.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(Часть PK)** Валютная пара. |
| **`"OpenTime"`** | `BIGINT` | **(Часть PK)** Начало минуты, Unix-мс. |
| `"MidPrice"` | `NUMERIC(18,8)` | Середина рынка: `(bid + ask) / 2`. |
| `"BestBid"` / `"BestAsk"` | `NUMERIC(18,8)` | Лучшие цены покупки и продажи. |
| `"SpreadAbs"` | `NUMERIC(18,8)` | Спред в единицах цены. |
| `"SpreadBps"` | `NUMERIC(12,4)` | Спред в базисных пунктах от `MidPrice` — стоимость мгновенного входа. |
| `"Imbalance"` | `NUMERIC(10,6)` | Дисбаланс книги по топ-20 уровням: `(bid - ask) / (bid + ask)`, от -1 до +1. Классический предиктор краткосрочного движения. |
| `"BidDepth01"` / `"AskDepth01"` | `NUMERIC(28,8)` | Объём заявок в пределах **0.1%** от `MidPrice`. |
| `"BidDepth05"` / `"AskDepth05"` | `NUMERIC(28,8)` | То же для **0.5%**. |
| `"BidDepth10"` / `"AskDepth10"` | `NUMERIC(28,8)` | То же для **1.0%**. «Толщина» рынка на трёх горизонтах. |
| `"MaxBidWall"` / `"MaxAskWall"` | `NUMERIC(28,8)` | Крупнейшая одиночная заявка на стороне. Берётся **максимумом** за минуту: важен сам факт, что стенка стояла. |
| `"MaxBidWallDistBps"` / `"MaxAskWallDistBps"` | `NUMERIC(12,4)` | Удалённость этой заявки от `MidPrice` в базисных пунктах. |
| `"UpdateCount"` | `INT` | Сколько раз книга обновилась за минуту — прокси нервозности рынка. |
| `"SampleCount"` | `INT` | Сколько срезов усреднено. Меньше ожидаемого (12 при съёме раз в 5 с) — были разрывы связи или ресинк, и фичи за эту минуту менее надёжны. |

> **Истории у этой таблицы нет и быть не может.** Архивов глубины по споту Binance не публикует (в `data/spot/daily/` только `aggTrades`, `klines`, `trades`; `bookDepth` есть лишь для фьючерсов). Данные идут с момента запуска коллектора.

### 3.6. `public."Processing_Watermarks"`

Watermark'и для streaming-процессов. По одной записи на каждый процесс-обработчик (`OhlcvAggregator`, `FeatureCalculator`).

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"ProcessName"`** | `VARCHAR(50)` | **(PK)** Имя процесса-обработчика. |
| `"LastProcessedTimestamp"` | `BIGINT` | Последняя обработанная позиция (Unix-мс или `OpenTime`). |
| `"Status"` | `VARCHAR(20)` | Текущий статус процесса. |
| `"LastUpdate_UTC"` | `TIMESTAMPTZ` | Время последнего обновления watermark'а. |

### 3.7. `public."HistoricalAudit_Watermarks"`

Состояние процесса исторической дозагрузки тиков по символу — где остановилась проверка.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Symbol"`** | `VARCHAR(20)` | **(PK)** Валютная пара. |
| `"LastChecked_TradeId"` | `BIGINT` | TradeId последней проверенной сделки. |
| `"LastChecked_Timestamp"` | `BIGINT` | Время последней проверенной сделки (Unix-мс). |
| `"Status"` | `VARCHAR(20)` | Текущий статус процесса. |
| `"RetryCount"` | `INT DEFAULT 0` | Количество повторных попыток после ошибок. |
| `"LastAttempt_UTC"` | `TIMESTAMPTZ NULL` | Время последней попытки. |

### 3.8. `public."DataQualityReports"`

**Карта покрытия проверками:** одна строка на пару «символ + месяц», перезаписывается при повторной проверке (upsert по `ix_dqr_symbol_month`). Отвечает на вопрос «какие месяцы истории проверены и какие из них грязные» — по ней видно состояние всей истории разом.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Id"`** | `INTEGER` | **(PK)** Автоинкрементный идентификатор отчёта. |
| `"Symbol"` | `VARCHAR(20)` | Валютная пара. Вместе с `PeriodMonth` — уникальная пара (`ix_dqr_symbol_month`), используется для upsert. |
| `"PeriodMonth"` | `DATE` | Первый день проверяемого месяца. |
| `"TradeCount"` | `BIGINT DEFAULT 0` | Количество сделок за период. |
| `"GapCount"` | `INT DEFAULT 0` | Количество разрывов последовательности `TradeId` за период. |
| `"InvalidPriceCount"` | `INT DEFAULT 0` | Количество сделок с `Price <= 0` или `Quantity <= 0`. |
| `"OutlierCount"` | `INT DEFAULT 0` | Количество сделок с ценой дальше 5σ от средней цены за период. |
| `"Status"` | `VARCHAR(10) DEFAULT 'ok'` | Итоговый статус периода: `'ok'`, `'warning'` (есть разрывы или выбросы) либо `'error'` (нет сделок за месяц или есть некорректные цены). |
| `"CheckedAt"` | `TIMESTAMPTZ DEFAULT now()` | Время выполнения проверки. |

Заполняется кнопкой «Построить отчёт» на странице `/DataQuality`. `GetUncheckedMonthsAsync` показывает месяцы, где данные в партициях есть, а отчёта ещё нет.

### 3.9. `public."DataQualityFindings"`

**Журнал проверок:** одна строка на каждую сработавшую проверку в конкретном прогоне, append-only. Отвечает на вопрос «что нашли вот в этом запуске». Заполняется кнопками групп проверок на странице `/DataQuality`; период одного запуска ограничен 31 днём.

| Поле | Тип | Описание |
| :--- | :--- | :--- |
| **`"Id"`** | `BIGINT` | **(PK)** Идентификатор находки (identity). |
| `"CheckGroup"` | `VARCHAR(32)` | Группа проверок: `trades`, `ohlcv`, `features`, `pipeline`. |
| `"CheckType"` | `VARCHAR(64)` | Конкретная проверка внутри группы (например, `trade_id_gaps`, `watermark_ahead_of_data`). |
| `"Symbol"` | `VARCHAR(20) NULL` | Валютная пара. `NULL` — проверка не привязана к символу (группа `pipeline`). |
| `"PeriodFrom"` | `TIMESTAMPTZ` | Начало проверенного периода. |
| `"PeriodTo"` | `TIMESTAMPTZ` | Конец проверенного периода. |
| `"Severity"` | `VARCHAR(10)` | `'ok'`, `'warning'` либо `'error'`. |
| `"Count"` | `BIGINT DEFAULT 0` | Сколько записей нарушают проверку. |
| `"Details"` | `JSONB NULL` | Подробности: список символов, значения watermark'ов и т.п. Состав зависит от `CheckType`. |
| `"CheckedAt"` | `TIMESTAMPTZ DEFAULT now()` | Время выполнения проверки. |

Каталог проверок и пороги — `src/BinanceDataCollector.Application/Common/DataQualityChecks.cs`, сами запросы — `DataQualityRepository.Run*ChecksAsync`.

---

## 4. Хранимые процедуры / функции

### 4.1. Сбор и управление символами

#### `public.sp_bulk_insert_trades(...)`
- **Задача:** Максимально быстро вставить пачку тиков в `"Trades"`.
- **Логика:** Принимает массивы (`UNNEST`), делает `INSERT ... ON CONFLICT ("TradeId", "Symbol", "TradeTime") DO NOTHING`.
- **Взаимодействие:** [Пишет] → `"Trades"`.

#### `public.sp_update_tracked_symbols(p_symbols VARCHAR[], p_max_missed_scans INT DEFAULT 3)`
- **Задача:** Атомарно обновить список активных пар.
- **Логика:**
  1. Паре, которой нет в новом списке, наращивает `MissedScans`. Деактивирует (`IsActive = FALSE`) только когда счётчик достиг `p_max_missed_scans`.
  2. Паре из списка — `INSERT ... ON CONFLICT DO UPDATE`: `IsActive = TRUE`, `LastScanned = NOW()`, `MissedScans = 0`.
- **Гистерезис.** Объём у пар вблизи порога отбора колеблется день ото дня, а собираются только активные пары. Без счётчика пара, просевшая под порог на один скан, теряла бы сбор до следующего попадания в ТОП — в истории образовалась бы дыра. Скан ежедневный, порог `3` даёт трёхдневное окно терпимости; пропуски должны идти подряд.
- **Взаимодействие:** [Читает и Пишет] → `"TrackedSymbols"`.

### 4.2. Агрегация

#### `public.sp_aggregate_new_trades(p_window_ms BIGINT DEFAULT 21600000)`
- **Задача:** Пересчитать свечи для минут, в которых есть необработанные тики.
- **Логика:**
  1. Находит **самый старый тик со статусом `'new'`** (частичный индекс `ix_trades_new_tradetime`) — это начало окна. Не watermark: данные, приехавшие «позади» уже обработанного участка (закрытие дыр, импорт архивов вразнобой), просто становятся новым минимумом.
  2. Берёт список символов окна из самих данных (не из `TrackedSymbols` — иначе тик по неотслеживаемой паре заблокировал бы окно навсегда).
  3. **Пересчитывает свечи целиком** из всех тиков минуты, включая уже обработанные. Порядок прихода данных перестаёт иметь значение, операция идемпотентна.
  4. Помечает свечи `'new'` (индикаторы по ним устарели) и тики — `'processed'`.
  5. Сдвигает watermark — только как индикатор прогресса.
- **Возвращает:** количество пересчитанных свечей.
- **Взаимодействие:** [Читает + Пишет] → `"Trades"`, `"Ohlcv_1min"`, `"Processing_Watermarks"`.

> Почему не watermark — см. [ADR 0004](../adr/0004-watermarking-idempotency.md). Коротко: watermark как фильтр корректности означал, что докачанная в дыру сделка **никогда** не попадёт в свечу.

### 4.3. Расчёт features

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

### 4.5. Партиционирование и ротация

Все растущие таблицы партиционированы **помесячно по одной сетке**. Дроп месяца — одна транзакция по всем таблицам сразу, поэтому свечей без тиков, из которых они посчитаны, не бывает. См. [ADR 0007](../adr/0007-size-based-retention-and-unified-partitioning.md).

| Таблица | Ключ партиционирования |
| :--- | :--- |
| `Trades` | `TradeTime` (BIGINT, Unix-мс) |
| `Ohlcv_1min` | `OpenTime` (BIGINT) |
| `Ohlcv_Features` | `OpenTime` (BIGINT) |
| `OrderBook_Features` | `OpenTime` (BIGINT) |
| `DataQualityReports` | `PeriodMonth` (DATE) |
| `DataQualityFindings` | `PeriodFrom` (TIMESTAMPTZ) |

`Processing_Watermarks` и `HistoricalAudit_Watermarks` не партиционируются — это состояние процессов, а не данные.

#### `public.fn_retention_floor_ms()`
- **Задача:** Граница ретенции — начало самой старой существующей партиции `Trades` (Unix-мс). Отдельно нигде не хранится, выводится из схемы.

#### `public.fn_partitioned_size_bytes()`
- **Задача:** Суммарный размер всех партиций (с индексами и TOAST). На нём основана ротация.

#### `public.sp_ensure_month_partitions(target_time BIGINT)`
- **Задача:** Создать партиции месяца **во всех пяти таблицах сразу**, если их ещё нет.
- **Барьер:** отказывается создавать партицию **ниже границы ретенции**. Этим одним guard'ом перекрывается повторная закачка дропнутого: аудитор, импорт архивов и закрытие дыр просто не найдут партиции для старых данных.
- **Вызывается:** из `TradeRepository.BulkInsertAsync` (через `sp_ensure_trades_partition`), из процедур агрегации и upsert'а фич, из `DataQualityRepository` перед записью.

#### `public.sp_ensure_trades_partition(target_time BIGINT)`
- Обратная совместимость: делегирует в `sp_ensure_month_partitions`.

#### `public.sp_rotate_partitions(p_max_bytes BIGINT, p_min_months_to_keep INT DEFAULT 6)`
- **Задача:** Ротация **по размеру диска, а не по календарю**.
- **Логика:** создаёт партиции на текущий и следующий месяц; затем, пока `fn_partitioned_size_bytes()` выше порога, дропает самый старый месяц во всех таблицах. После дропа подтягивает `HistoricalAudit_Watermarks` до новой границы, чтобы аудитор не искал дыры в удалённом.
- **Предохранитель:** месяцы свежее `p_min_months_to_keep` не дропаются никогда, даже под давлением диска.
- **Порог** приходит из конфигурации (`RetentionSettings` в `appsettings.json`), а не зашит в SQL.
- **Вызывается:** ежедневно `PartitionMaintenanceWorker`.

**Старт истории — 01.01.2026.** Партиции заведены с 2026-01, поэтому на свежей базе граница ретенции = 2026-01, и данные за более ранние даты записать нельзя.

---

## 5. Как процессы находят необработанное

Обработка идёт **по статусу, а не по времени** — это ключевое свойство пайплайна.

**Механика.** В `"Trades"` и `"Ohlcv_1min"` есть колонка `ProcessingStatus` с дефолтом `'new'`. Обработчик выбирает записи со статусом `'new'`, обрабатывает их и переводит в `'processed'`:

| Процесс | Читает `'new'` из | Пишет | Ставит `'new'` в |
| :--- | :--- | :--- | :--- |
| `sp_aggregate_new_trades` | `"Trades"` | `"Ohlcv_1min"` | `"Ohlcv_1min"` |
| `FeatureCalculatorWorker` + `sp_upsert_ohlcv_features` | `"Ohlcv_1min"` | `"Ohlcv_Features"` | — |

Свеча, пересчитанная агрегатором, снова получает статус `'new'`: индикаторы, посчитанные по её прошлой версии, устарели и должны быть пересчитаны.

**Почему по статусу.** Тики приезжают не по порядку: закрытие дыры за март может прийти после того, как обработан июль. Фильтр «время >= watermark» такую сделку **никогда** бы не увидел, и дыра в свечах осталась бы навсегда. Статус от порядка прихода не зависит. Подробности и отвергнутая альтернатива — [ADR 0004](../adr/0004-watermarking-idempotency.md).

**Идемпотентность.** Агрегатор пересчитывает минуту целиком из всех её тиков (а не досчитывает дельту), запись идёт через `ON CONFLICT DO UPDATE`. Повторный прогон на тех же данных даёт тот же результат.

**`"Processing_Watermarks"`.** Таблица осталась как **индикатор прогресса** для мониторинга: `LastProcessedTimestamp` показывает, до какого времени дошёл процесс. На выборку данных она не влияет.

---

## 6. Индексы

| Индекс | Таблица | Колонки | Зачем |
| :--- | :--- | :--- | :--- |
| `ix_trades_symbol_tradetime` | `Trades` (партиционированный, индекс на родителе `ON ONLY`, per-партиция — `Trades_YYYY_MM_Symbol_TradeTime_idx`) | `(Symbol, TradeTime DESC)` | Основной для выборки сделок по паре за период (агрегация, аудит). |
| `ix_trades_processingstatus` | `Trades` (партиционированный, индекс на родителе `ON ONLY`, per-партиция — `Trades_YYYY_MM_ProcessingStatus_idx`) | `(ProcessingStatus)` частичный, `WHERE ProcessingStatus = 'new'` | Watermark-обработка: быстрый поиск необработанных тиков без сканирования уже обработанных. |
| `IX_TrackedSymbols_IsActive` | `TrackedSymbols` | `(IsActive)` | Быстрая выборка активных пар. |
| `IX_Ohlcv_1min_ProcessingStatus_OpenTime` | `Ohlcv_1min` | `(ProcessingStatus, OpenTime)` | Feature-pipeline: поиск свечей с `ProcessingStatus = 'new'`. |
| `IX_HistoricalAudit_Watermarks_Status` | `HistoricalAudit_Watermarks` | `(Status)` | Выборка символов в нужном статусе аудита. |
| `ix_dqr_symbol_month` (UNIQUE) | `DataQualityReports` | `(Symbol, PeriodMonth)` | Один отчёт на пару "символ+месяц"; обеспечивает `ON CONFLICT` при upsert. |
| `ix_dqr_status` | `DataQualityReports` | `(Status)` частичный, `WHERE Status <> 'ok'` | Быстрая выборка проблемных отчётов (`warning`/`error`). |
| `ix_dqr_checked_at` | `DataQualityReports` | `(CheckedAt DESC)` | Выборка последних по времени проверок. |
| `ix_dqf_checked_at` | `DataQualityFindings` | `(CheckedAt DESC)` | Основная выборка страницы: последние находки сверху. |
| `ix_dqf_severity` | `DataQualityFindings` | `(Severity)` частичный, `WHERE Severity <> 'ok'` | Быстрый доступ к проблемам. |
| `ix_dqf_group_symbol` | `DataQualityFindings` | `(CheckGroup, Symbol)` | Фильтрация по группе проверок и символу. |

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
- **DataManager:** `/hangfire`, доступ только для роли `Admin` (`AdminHangfireAuthorizationFilter`).

---

## 8. Связи между таблицами

В схеме **нет ни одного `FOREIGN KEY`**. Все связи — логические, через совпадение полей:

- `Trades.Symbol` ↔ `TrackedSymbols.Symbol`
- `Ohlcv_1min(Symbol, OpenTime)` ↔ `Ohlcv_Features(Symbol, OpenTime)`
- `HistoricalAudit_Watermarks.Symbol` ↔ `TrackedSymbols.Symbol`
- `DataQualityReports.Symbol` ↔ `TrackedSymbols.Symbol`
- `DataQualityFindings.Symbol` ↔ `TrackedSymbols.Symbol` (может быть `NULL` — проверки группы `pipeline` не привязаны к символу)
- `Processing_Watermarks.ProcessName` — имена процессов жёстко прописаны в коде (`OhlcvAggregator`, `FeatureCalculator`).

**Почему так.** Целевая нагрузка — bulk-вставки тиков (десятки тысяч в секунду). FK-проверки на каждой вставке неприемлемы. Целостность поддерживается на уровне приложения: воркеры не пишут в таблицы для пар, которых нет в `TrackedSymbols`, а sp-процедуры используют `ON CONFLICT DO NOTHING` / `DO UPDATE` для устойчивости к гонкам.

---

## 9. Auth-схема

Документация по подсистеме аутентификации и авторизации — в [`docs/common/auth/`](./auth/). Описанная там модель данных — это **спецификация**. В текущей схеме БД таблиц авторизации нет.

В `BinanceDataCollector.DataManager` работает только аутентификация через **Azure AD B2C** (OIDC + Cookie auth). Собственной auth-схемы в БД на текущий момент нет.

---

## Скрипт схемы

Полный DDL живёт в едином baseline **[`docker/postgres/init/02_schema.sql`](../../docker/postgres/init/02_schema.sql)** (`pg_dump --schema-only`), который автоматически применяется на чистом томе при инициализации PostgreSQL (`docker-entrypoint-initdb.d`). Отдельной копии схемы в этом документе намеренно нет, чтобы не расходиться с baseline.
