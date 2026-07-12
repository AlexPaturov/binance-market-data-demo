# План: перенос initial load с dev на прод

Уточняет и заменяет `docs/TODO_POST_INITIAL_LOAD.md` Шаг 2 (там ещё фигурирует VirtualBox VM — устарело, dev теперь Ubuntu-native, диск подключён напрямую). Решение принято 2026-07-12: не дожидаться конца распаковки на dev, перенести остаток (Фазы 3–6 из `docs/INITIAL_DATA_LOAD.md`) на прод. CI/CD (`deploy.yml`) не трогаем — все действия ниже делаются вручную по SSH, работа идёт в отдельной ветке.

## Факт на 2026-07-12 (проверено)

- `Trades` заполнены за 6 из ~19 месяцев (апр, авг–дек 2025), 556 ГБ из 3.6 ТБ на диске `/mnt/ext`. `Ohlcv_1min`/`Ohlcv_Features`/`Processing_Watermarks`/`HistoricalAudit_Watermarks` — пустые (Фазы 4–6 не начинались).
- Очередь `archive_import` ещё не пуста: `~/bdc_data/Trades/Downloaded` — 3679 ZIP (18 ГБ, ждут распаковки), `~/bdc_data/Trades/Unpacked` — 475 CSV (29 ГБ, в процессе импорта). Это и есть "недокачанные архивы" — не оборванные закачки, а хвост очереди Фазы 3.
- `partition-maintenance` (rotation) уже отключена в коде (`HangfireJobsService.cs`).
- Прод: GMKtec G2 mini-PC, Intel N150, Ubuntu 24.04, весь стек в Docker (`docker-compose.yml` + `docker-compose.prod.yml`), realtime-трафик прод сейчас не держит.
- `binancecollector_postgres_data` на проде сейчас — **named volume** (не bind mount на внешний диск), это то, что меняется на Шаге 3. Старый volume после переноса **удаляется**, не хранится как откат.
- `docker/docs/README_DO_NOT_TOUCH.md` прямым текстом запрещает трогать external volumes (`postgres_data` в их числе) — Шаг 3 меняет схему хранения насовсем, поэтому обновляет и сам README, а не оставляет описание устаревшим.
- **Импорт crash-safe, ждать дренажа очереди не нужно** (проверено по коду): `CsvImportWorker.ImportFromCsvAsync` удаляет папку с CSV только после успешной вставки всех строк; `ArchiveUnpackerWorker.UnpackArchiveAsync` удаляет ZIP только после успешной распаковки и постановки джобы импорта. `BulkInsertAsync` → `sp_bulk_insert_trades` → `ON CONFLICT ("TradeId","Symbol","TradeTime") DO NOTHING`. Значит обрыв файла на середине (`AutomaticRetry` либо просто новый старт) перечитывает файл целиком, уже вставленные строки дедуп отбрасывает, недостающие доимпортирует — без потерь и без дублей. Можно останавливать Worker в любой момент, не дожидаясь опустошения очереди.
- **Очередь Hangfire едет вместе с диском и в целом отработает сама** (`market_analytics_jobs` — та же Postgres-инстанция, тот же диск, что и `market_analytics`). Разница dev/прод только в `ArchivesSettings.BasePath` (`/home/lex/bdc_data` на dev vs `/opt/bdc_data` по умолчанию на проде — класс `ArchivesSettings.cs`), относительная структура одна и та же (`Trades/Downloaded`, `Trades/Unpacked`). Это влияет на 2 типа джоб по-разному:
  - `UnpackArchiveAsync` (3679 шт., ZIP) — в очереди только голое имя файла, путь пересчитывается через `IPathProvider` в момент выполнения джобы, на актуальном для прода конфиге. Проблемы нет, ничего доделывать не нужно — просто убедиться что файлы физически лежат в `.../Trades/Downloaded` внутри volume (Шаг 2).
  - `ImportFromCsvAsync` (475 шт., CSV) — `ArchiveUnpackerWorker` резолвит `GetTradeUnpackedPath()` один раз, в момент распаковки (уже прошла на dev), и кладёт получившийся **абсолютный** dev-путь (`/home/lex/bdc_data/Trades/Unpacked/...`) прямо в аргумент джобы. Эта строка не пересчитается. Решение — не пересоздавать джобы, а сделать так, чтобы старый путь тоже существовал на проде и указывал на те же файлы: смонтировать тот же volume `bdc_data` в контейнер `bdc_worker` вторым путём (см. Шаг 2).

## Шаг 1 — Остановить Worker на dev

**Кто:** разработчик

- Остановить Worker/DataManager (Rider run configuration — они на dev нативные процессы, не контейнеры). Можно в любой момент, ждать завершения текущего файла не нужно — см. факт про crash-safety выше.
- Не удалять и не чистить `~/bdc_data/*` — переносится на прод как есть, это и есть точный остаток работы.

**Критерий завершения:** Worker остановлен.

## Шаг 2 — Перенести недокачанные архивы на прод

**Кто:** разработчик
**Канал:** Tailscale (`100.96.120.16`), тот же, что уже используется для доступа к БД.

```bash
rsync -avz --progress \
  ~/bdc_data/Trades/Downloaded/ \
  lex@100.96.120.16:/tmp/bdc_data_transfer/Trades/Downloaded/

rsync -avz --progress \
  ~/bdc_data/Trades/Unpacked/ \
  lex@100.96.120.16:/tmp/bdc_data_transfer/Trades/Unpacked/
```

На проде `binancecollector_bdc_data` — named volume без известного bind-пути на хосте. Перелить из staging-директории в сам volume теми же относительными путями, что использует `PathProvider` (`TradeArchivesRelativePath`/`TradeUnpackedRelativePath` = `Trades/Downloaded`/`Trades/Unpacked`, `ArchivesSettings.cs`), не трогая identity volume'а:

```bash
docker run --rm \
  -v binancecollector_bdc_data:/dest \
  -v /tmp/bdc_data_transfer:/src \
  alpine sh -c "mkdir -p /dest/Trades/Downloaded /dest/Trades/Unpacked && \
                cp -r /src/Trades/Downloaded/. /dest/Trades/Downloaded/ && \
                cp -r /src/Trades/Unpacked/. /dest/Trades/Unpacked/"
```

Добавить в `docker-compose.prod.yml` у `bdc_worker` второй mount того же volume — иначе 475 уже поставленных в очередь `ImportFromCsvAsync`-джоб (аргумент — абсолютный dev-путь `/home/lex/bdc_data/Trades/Unpacked/...`, зашит на момент распаковки) не найдут файл:

```yaml
bdc_worker:
  volumes:
    - bdc_data:/opt/bdc_data        # уже есть — так резолвит prod-конфиг (BasePath по умолчанию)
    - bdc_data:/home/lex/bdc_data   # добавить — так же резолвит dev-конфиг, чтобы старые пути в очереди сработали
```

**Критерий завершения:** количество файлов в volume совпадает с dev (3679 ZIP + 475 CSV), оба пути (`/opt/bdc_data/...` и `/home/lex/bdc_data/...`) внутри контейнера `bdc_worker` видят одни и те же файлы, staging-директория на проде можно удалить.

## Шаг 3 — Физическая замена диска

**Кто:** разработчик

1. Остановить `bdc_db` на dev: `docker compose -f docker-compose.yml -f docker-compose.db.yml down`.
2. `sudo umount /mnt/ext`, физически отключить внешний диск от dev-машины.
3. Подключить диск к GMKtec, примонтировать (`mount`), добавить в `/etc/fstab` по UUID (`25ddc534-13e6-479e-8392-a4487a975c80` — тот же диск, UUID не изменится).
4. В `docker-compose.prod.yml` заменить определение volume:
   ```yaml
   volumes:
     postgres_data:
       external: true
       name: binancecollector_postgres_data
   ```
   на bind mount у сервиса `bdc_db`:
   ```yaml
   bdc_db:
     volumes:
       - /mnt/ext/postgres_data:/var/lib/postgresql/data
   ```
5. `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d bdc_db`.
6. Проверить подключение: `docker exec bdc_db psql -U bindatacoll -d market_analytics -c "\dt"` — должны быть видны партиции `Trades_2025_01`...`Trades_2026_12`.
7. После подтверждения, что bind mount работает и данные на месте — удалить старый named volume: `docker volume rm binancecollector_postgres_data`.
8. Обновить документацию под новую схему хранения (сразу, не откладывая):
   - `docker/docs/README_DO_NOT_TOUCH.md` — заменить `binancecollector_postgres_data` в списке "external volumes, трогать запрещено" на новый пункт про сам внешний диск `/mnt/ext` (не отмонтировать, не менять fstab-запись без понимания).
   - `docker/docs/README_VOLUMES.md` — сейчас пустой файл, описать актуальную схему (какие volume named, какой — bind mount, и почему).
   - `docs/prod/ARCHITECTURE_PROD.md` §5 — `postgres_data` теперь bind mount на внешний диск, а не named volume.

**Проверить отдельно:** прод-конфиг Postgres задаёт `-c password_encryption=md5`, диск приезжает с dev, где такой настройки нет (вероятно `scram-sha-256` по умолчанию для Postgres 16). Формат хранимого хэша пароля роли не меняется от текущего значения GUC — но подключение (`pgbouncer` → `bdc_db`, `AUTH_TYPE=scram-sha-256`) стоит проверить сразу после старта, а не постфактум.

**Критерий завершения:** `bdc_db` на проде здоров (`pg_isready`), партиционированная схема видна, подключение через PgBouncer работает, старый volume удалён, README обновлены.

## Шаг 4 — Продолжить Фазы 3–6 из `docs/INITIAL_DATA_LOAD.md`, но на проде

**Кто:** разработчик

1. Деплой текущего кода на прод обычным способом (ветка → мердж в master → штатный CI, без изменений в `deploy.yml`).
2. **Фаза 3 (продолжение):** убедиться что архивы из Шага 2 на месте в `bdc_data` и второй mount (`/home/lex/bdc_data`) подключён. Очередь `archive_import` (приехала вместе с диском) дорабатывает сама — ничего в Archive UI нажимать не нужно, ни ZIP-, ни CSV-джобы пересоздавать не требуется (см. Шаг 2).
3. **Фаза 4 (историческая агрегация):** тот же PL/pgSQL-скрипт из `INITIAL_DATA_LOAD.md`, выполнить через psql/DBeaver уже на проде, когда Фаза 3 дойдёт до конца.
4. **Фаза 5:** SQL для заполнения `HistoricalAudit_Watermarks` — на проде.
5. **Фаза 6:** включить recurring jobs (`ohlcv-aggregator`, `feature-calculator`, `historical-audit`, `quick_audit`) — раскомментировать в `HangfireJobsService.cs`, задеплоить обычным способом (не CI-правка, обычный код).

**Критерий завершения:** совпадает с точками синхронизации в `INITIAL_DATA_LOAD.md` (`archive_import` пуст, скрипт агрегации вывел `Done`, вотермарки проставлены, recurring jobs видны в Hangfire Dashboard).

## Шаг 5 — Привести dev в целевое состояние

**Кто:** разработчик, после подтверждения что прод стабилен

- Диск уехал на прод — dev остаётся без `/mnt/ext`. Поднять `bdc_db` на dev с чистым локальным путём (без внешнего диска), схема — тот же `docker/postgres/init/02_schema.sql` (партиционированная).
- Держать 2 актуальные rotating-партиции для тестов — без полноценного `DevSyncService` из `docs/TODO_POST_INITIAL_LOAD.md` Шаг 5 (это отдельная 3–4-дневная задача, не обязательна для завершения проекта; при необходимости делается позже).

---

# Data Quality: сценарии порчи данных

Порядок работы над остальным MVP (авторизация, тесты, пайплайн, документация) выполнен и закрыт. Этот раздел — рабочий список сценариев порчи данных для будущей стадии тест-сценариев: что проверять, где это уже проверяется, и что ещё нужно добавить.

## Что уже есть

- `DataQualityRepository.CheckSymbolMonthAsync` — месячный отчёт по символу: `GapCount` (разрыв `TradeId`), `InvalidPriceCount` (`Price <= 0 OR Quantity <= 0`), `OutlierCount` (цена дальше 5σ от средней), пишется в `"DataQualityReports"`.
- `DataQualityRepository.GetReportsAsync(symbol?, status?)` — выборка отчётов, есть, но нигде не подключена (не зарегистрирована в DataManager DI, нет контроллера/страницы). Подключить — задача на стадию тест-сценариев.
- Разрывы по `TradeId` в окне — `TradeRepository.FindGapsInTimeWindowAsync` (используется `QuickAuditorWorker`), `AnalysisRepository.FindGapsInWindowAsync` → `sp_find_trade_id_gaps_in_window` (используется `HistoricalAuditorWorker`).

## 1. `"Trades"` (сырые тики)

| # | Сценарий | Статус | Проверка |
|---|---|---|---|
| 1 | `Price <= 0` / `Quantity <= 0` | есть | `DataQualityRepository`, месячно |
| 2 | Ценовой выброс (5σ) | есть | `DataQualityRepository`, месячно; порог фиксирован, можно уточнить (MAD вместо std, окно короче месяца) |
| 3 | Разрыв последовательности `TradeId` | есть, но не на границе месяца | `CheckSymbolMonthAsync` не видит разрыв между последним тиком месяца N и первым тиком месяца N+1 — добавить пограничную проверку |
| 4 | Разрыв **по времени**, не по `TradeId` | не подключено | `sp_find_trade_gaps`/`sp_find_gaps_in_window` есть в схеме, не вызываются из кода — решить, подключать или удалить как мёртвый код |
| 5 | Дубликат: тот же `TradeId`+`Symbol`, другой `TradeTime` (PK — тройка, не пара) | нет | `GROUP BY TradeId, Symbol HAVING COUNT(DISTINCT TradeTime) > 1` |
| 6 | `IsMyTrade=true`, но `Commission`/`CommissionAsset` не согласованы | нет | точечная проверка полей для собственных сделок |
| 7 | `TradeTime` вне разумного диапазона (в будущем, раньше `TrackedSymbols.DateAdded`) | нет | сверка `TradeTime` с `now()` и `DateAdded` |
| 8 | Сделка по символу, которого нет в `TrackedSymbols` | нет | `LEFT JOIN Trades.Symbol → TrackedSymbols.Symbol IS NULL` |

## 2. `"Ohlcv_1min"` (агрегированные свечи)

Пока не проверяется вообще — ни в БД, ни в приложении.

| # | Сценарий | Проверка |
|---|---|---|
| 9 | `HighPrice < LowPrice`, либо `Open`/`Close` вне `[Low, High]` | `CHECK` constraint на таблице |
| 10 | `Volume < 0`, либо `Volume = 0` при наличии тиков за минуту | сверка с `COUNT`/`SUM` по `Trades` за то же окно |
| 11 | `OpenTime` не кратен 60000 мс | `CHECK ("OpenTime" % 60000 = 0)` |
| 12 | Пропущенные минуты у активного символа | тот же `LAG`-паттерн, что и для `TradeId`, но по `OpenTime` с шагом 60000 |
| 13 | Расхождение агрегата со свежим пересчётом из `Trades` | периодическая сверка пересчёта против сохранённого |

## 3. `"Ohlcv_Features"` (индикаторы)

Тоже без проверок.

| # | Сценарий | Проверка |
|---|---|---|
| 14 | `RSI_14` вне `[0, 100]` | `CHECK` constraint |
| 15 | `MA_*` сильно расходится с ценой свечи в тот же `OpenTime` | проверка относительного отклонения `MA` от `ClosePrice` |
| 16 | `CVD` делает скачок, не объяснимый `Volume`/`IsBuyerMaker` из `Ohlcv_1min` | проверка дельты `CVD` против `Volume` |
| 17 | Свеча `processed`, но строки в `Ohlcv_Features` нет (тихая потеря, FK нет) | `LEFT JOIN Ohlcv_1min → Ohlcv_Features` по `(Symbol, OpenTime)` |
| 18 | Осиротевшая строка в `Ohlcv_Features` без `Ohlcv_1min` | обратный `LEFT JOIN` |

## 4. Watermark / состояние пайплайна

Самое опасное — тихая потеря данных, а не видимая ошибка.

| # | Сценарий | Проверка |
|---|---|---|
| 19 | `Processing_Watermarks.LastProcessedTimestamp` обогнал реальные данные — выборка всегда `>= watermark`, всё до этой отметки выпадает из обработки молча | сравнить watermark с независимым пересчётом (MIN нового `TradeTime`/`OpenTime`, который должен был быть обработан) |
| 20 | Watermark завис, хотя новые `'new'`-записи есть | возраст `LastUpdate_UTC` относительно `now()` и `MAX(...)` с `ProcessingStatus='new'` |
| 21 | `HistoricalAudit_Watermarks.Status='Failed'` с исчерпанным `RetryCount` — по текущему запросу такой символ больше никогда не попадёт в выборку | алерт на символы с `RetryCount >= MaxRetries` |
| 22 | `TrackedSymbols.IsActive` не согласован с состоянием аудита | сверка `IsActive` против статусов аудита |
| 23 | `sp_rotate_trades_partition` дропает партиции старше 13 месяцев без проверки, что данные заархивированы | проверка перед `DROP TABLE`, что партиция экспортирована (если такой процесс есть) |

## Реализация (когда дойдём)

- Дешёвые структурные инварианты (#9, #11, #14) — `CHECK` constraint в БД, ловят порчу в момент записи.
- Сверки, требующие агрегации/сравнения между таблицами (остальное) — расширение `DataQualityRepository` / отдельная джоба, по образцу уже существующего пайплайна.
- Подключить `DataQualityRepository.GetReportsAsync` к DataManager (DI + контроллер/страница) — сейчас это единственный способ увидеть отчёты, кроме логов Worker'а.
