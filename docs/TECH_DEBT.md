# Технический долг проекта BinanceDataCollector

> Документ фиксирует известные проблемы, расхождения и подозрения,
> найденные в процессе ревизии проекта (май 2026).
> 
> **Принцип:** записываем, потом разбираемся. Не лезем в работающее без понимания зачем оно так сделано.

---

## 1. CI/CD и deploy.yml

### Безопасность (требует разбора, не срочно если прод стоит)

- **Пароль Seq захардкожен в `deploy.yml`:** `SEQ_ADMIN_USER=lex`, `SEQ_ADMIN_PASS=lex`. Должны быть в GitHub Secrets.
- **`pull_request` триггер запускает деплой на прод** — `on:` содержит и `push`, и `pull_request` в `master`. Любой PR → авто-деплой. Возможно артефакт из шаблона, нужно решить осознанно.
- **Утечка секретов в логи Actions:** `sed` маскирует только `PASSWORD=*`, но `CLOUDFLARE_TUNNEL_TOKEN`, `AUTH_B2C_CLIENT_SECRET` попадают в лог открытым текстом.

### Конфигурация (статус "нужно проверить, прежде чем менять")

- **`docker compose pull/up -d` без `-f` флагов** — Compose может брать конфиги не оттуда, откуда мы думаем. Может на сервере есть `docker-compose.override.yml` который меняет картину.
- **Нет `:latest` тэга образов** — откат только ручной через правку `APP_VERSION`. Возможно сделано намеренно (детерминированность).
- **`SERILOG_SEQ_URL=http://seq:5341`** в workflow — имя контейнера в проде `bdc_seq`, не `seq`. Возможно перебивается на уровне переменных окружения Worker'а (`http://bdc_seq:80`), и тогда переменная просто не используется.
- **Шаг `List files for debugging: ls -R`** засоряет лог. Возможно остался от первой настройки.
- **Нет `dotnet test` в pipeline** — тесты в репо есть, но в CI не запускаются.
- **`runs-on: self-hosted` без labels** — если будет несколько раннеров, поведение станет недетерминированным.

### Вопросы которые надо проверить на сервере

- Что лежит в `/opt/BinanceCollector/docker/compose/` — правда ли там только `docker-compose.prod.yml` или есть `docker-compose.yml` / `docker-compose.override.yml`?
- Какой реально файл подцепляется при `docker compose up -d` в этой директории?
- Какие переменные окружения реально доходят до Worker'а (есть ли пересечения с конфигами в образе)?

---

## 2. SQL-схема и репозиторий

### Расхождения между прод-БД и репо

- **`sp_aggregate_trades_to_ohlcv(BIGINT, BIGINT)`** существует на проде, но её DDL **нет ни в одном коммите**. Тело найдено только в untracked-файле `sqlScripts/prod_schema_2026-05-09.sql`.
- **`Processing_Watermarks` имеет 4 колонки** (`ProcessName`, `LastProcessedTimestamp`, `Status`, `LastUpdate_UTC`), но в `tests/schema.sql` — только 2.
- **`Audit_Blocks`** удалена с прода, но DDL и обвязка в коде остались.
- **`sp_find_trade_id_gaps_in_window`** вызывается из `HistoricalAuditRepository`, но её определения нет в `sqlScripts/`.
- **Индекс `ix_trades_tradetime_desc`** существует на проде, но не в репо.
- **`sqlScripts/ddl/dbFromScratch.sql` устарел** — не содержит `ProcessingStatus`, новых таблиц, новых индексов. Запуск на живой БД откатит схему.

### Мёртвый код в репозитории

Подтверждено что не используется (включая проверку с раскомментированным `BinanceCollectorWorker`):

- Таблица `Audit_Blocks`
- Методы `AuditRepository.{GenerateNewAuditBlocks, GetBlocksToProcess, UpdateBlockStatus}Async`
- `AnalysisRepository.GetDataQualityStatsAsync` + соответствующая SQL-функция
- `TradeRepository.GetGapsForSymbolDayAsync` (только в тестах)
- `TradeRepository.GetTradeIdsInWindowAsync`
- SQL-функция `sp_get_data_quality_stats`
- SQL-функция `sp_claim_new_ohlcv_for_features` (заменена на raw SQL в `OhlcvRepository`)
- SQL-функции `sp_find_gaps_in_window`, `sp_find_trade_gaps` (в `sqlScripts/`, нет вызовов из C#)

### Подозрения требующие проверки

- **`Processing_Watermarks`: 2 vs 4 колонки** — какая версия фактически на dev/prod? Проверить через `\d "Processing_Watermarks"` на каждом сервере отдельно.
- Поведение `sp_aggregate_trades_to_ohlcv` после переписывания — `Volume` теперь `EXCLUDED.Volume` (полный пересчёт), раньше было суммирование. Возможен баг в краевых случаях с задержкой тиков.
- **Расхождение имени колонки и константы для MA 200-недель:** константа `Ma200wPeriods = 2016000` в `IndicatorService.cs`, а колонка в БД и параметр функции называются `MA_201600` (один ноль потерян). Нужно выровнять — либо переименовать колонку, либо константу. Затрагивает: `IndicatorService.cs`, `sp_upsert_ohlcv_features`, таблицу `Ohlcv_Features`, `FeatureRepository.cs`.
- **MA 2Y и MA 200W вычисляются на 1-минутных свечах — архитектурная ошибка:** эти макро-индикаторы требуют 1,051,200 и 2,016,000 1-минутных свечей соответственно (~2 и ~3.8 года), поэтому значения будут `NULL` до тех пор, пока система не проработает несколько лет. Семантически верный подход — считать их на **дневных свечах** (SMA(730) и SMA(200) на `Ohlcv_1d`): нужно 730 и 200 дней данных, что достижимо. Требует: новую таблицу `Ohlcv_1d`, агрегатор минут→дни, обновление `IndicatorService.cs` и `sp_upsert_ohlcv_features`. Сейчас не блокирует — колонки просто всегда `NULL`.

---

## 3. Hangfire конфигурация

- **Гонка при создании схемы:** только Worker настроен с явными `PostgreSqlStorageOptions` (`PrepareSchemaIfNecessary = true`, `SchemaName = "hangfire"`). DataManager берёт дефолты. Если на чистой БД случайно стартанёт первым DataManager — схема создастся с дефолтными параметрами.
- **Hangfire Dashboard в DataManager использует `AllowAllConnectionsFilter`** — буквально пропускает всех. В проде защита должна быть на уровне Traefik/Cloudflare. Если эта защита не настроена — дашборд открыт публично.

---

## 4. Состояние BinanceCollectorWorker

- **Закомментирован в `Program.cs:117`** — сейчас realtime-сбор тиков отключён. Тики поступают только через Hangfire-джобы (`OnlineArchiveImportWorker`, `CsvImportWorker`, `FillGapWorker`).
- Намеренно ли это? Когда планируется включить обратно?
- **Graceful cancellation CSV import логируется как ошибка:** при штатной остановке Worker через `Ctrl+C` активный `CsvImportWorker.ImportFromCsvAsync` получает cancellation token, `ArchiveService.ParseTradesFromCsvStreamAsync` выбрасывает `OperationCanceledException`, а worker пишет это как `[ERR] Error importing from file ...`. По смыслу это controlled shutdown, не unexpected import failure. Нужно отдельно обработать `OperationCanceledException`/Hangfire shutdown token и логировать как `Information`/`Warning`, сохранив корректный статус job для повторного запуска. Это снижает шум в логах и убирает ложный portfolio-сигнал о падении импорта.

---

## 5. Структура репозитория

- **`docker-compose.override.yml`** — единственная копия в локальном бэкапе, в активном репо отсутствует. Решено пока не трогать. **(Решение от 2026-05-09)**
- **`sqlScripts/`** — устаревший. Реально работающая схема только в `sqlScripts/prod_schema_2026-05-09.sql` (untracked) и в живой прод-БД.
- **`tests/BinanceDataCollector.Infrastructure.Tests/schema.sql`** — последний раз обновлялся 14 ноября 2025, устарел минимум на 6 месяцев.
- **`postgres-config/custom.conf`** — пустой файл, артефакт.
- **Ветки `origin/docker-refactor` и `origin/test/ci`** — обе полностью смерджены в master, отстают на 49+ коммитов. Ничего нового не содержат, можно удалить.

---

## 7. Конфигурация запуска (DataManager и Worker)

- **`ConfigureKestrel` конфликтует с `ASPNETCORE_URLS`:** в обоих `Program.cs` явно вызывается `builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(...))`. Kestrel при этом игнорирует `applicationUrl` из `launchSettings.json` и `ASPNETCORE_URLS` из docker-compose, выдавая warning при каждом старте: `Overriding address(es) 'http://localhost:7002'`. Нужно убрать `ConfigureKestrel` — в dev порт задаётся через `applicationUrl` в launchSettings, в проде через `ASPNETCORE_URLS` в docker-compose (уже настроено).

---

## 10. Пометка тиков обработанными — 90% времени агрегации

**Проблема:** `sp_aggregate_new_trades` тратит основное время не на пересчёт свечей, а на `UPDATE ProcessingStatus = 'processed'` по тикам окна. Замер на реальных данных (окно 6 часов, 1.4 млн тиков):

| Этап | Время |
|---|---|
| Список символов окна | 0.4 с |
| Пересчёт свечей | 1.7 с |
| **UPDATE пометки статуса** | **11.8 с** |

Каждый тик переписывается ровно один раз, но это MVCC-перезапись строки плюс обновление трёх индексов. Для месяца данных (~316 млн тиков) это ~45 минут чистого UPDATE и соответствующий bloat — таблица временно раздувается вдвое, пока не отработает autovacuum.

**Решение:** отказаться от статус-колонки в пользу очереди «грязных минут». Таблица `(Symbol, OpenTime)`, куда `sp_bulk_insert_trades` дописывает затронутые минуты при вставке (несколько строк на пачку в 10 тысяч тиков). Агрегатор забирает пачку минут, пересчитывает свечи, удаляет их из очереди. `UPDATE` по `Trades` исчезает совсем — вместе с bloat'ом.

Требует правки пути вставки (`sp_bulk_insert_trades`), поэтому отложено: текущая реализация корректна, просто платит за это временем.

---

## 9. Дублирование SQL проверок качества данных

**Проблема:** разрывы `TradeId`, невалидные цены и 5σ-выбросы считаются **двумя разными SQL-запросами** в одном и том же `DataQualityRepository`:

- `CheckSymbolMonthAsync` — для `"DataQualityReports"` (карта покрытия: один отчёт на пару «символ + месяц»),
- `RunTradesChecksAsync` — для `"DataQualityFindings"` (журнал: находки за произвольный период до 31 дня).

Обе таблицы оставлены осознанно (решение 2026-07-13): они отвечают на разные вопросы — «какие месяцы истории проверены и какие грязные» против «что нашли в этом прогоне». Но логика подсчёта одних и тех же дефектов продублирована и может разъехаться при правках.

**Решение:** свести обе к одному SQL — например, `CheckSymbolMonthAsync` считает через тот же запрос, что и `RunTradesChecksAsync`, и просто сворачивает результат в формат отчёта. Обе ветки покрыты тестами (`DataQualityRepositoryTests`), так что расхождение поймается, но лучше его не допускать.

---

## 8. Dev/Prod синхронизация БД (rolling window)

**Проблема:** для разработки индикаторов нужны полные тиковые данные (Trades) — без них невозможно тестировать intra-candle стратегии и скальпинг. Полное зеркало прода (~1 TB+) нецелесообразно на dev-машине.

**Решение:** rolling window с инкрементальной подкачкой с прода через Tailscale.

**Параметры:**
- Окно: последние 3 месяца (rolling)
- Объём: ~110-140 GB (Trades) + ~10 GB (Ohlcv_1min, Ohlcv_Features) = **150-200 GB** под dev-БД
- Скорость синхронизации: ~1.5-2 GB/день нового прироста → 3-5 мин на день отставания

**Что нужно реализовать:**
- Watermark per symbol в dev (`DevSyncWatermarks` — последний синхронизированный `TradeTime`)
- Startup-процедура: сравниваем watermark dev vs prod, тянем дельту через Tailscale (100.96.120.16)
- Rolling cleanup: удаляем из dev строки старше 3 месяцев после синхронизации
- Только после успешной синхронизации — старт воркеров

**Компоненты:** отдельный сервис или hosted service в Worker с запуском на старте приложения.

---

## 6. План разбора (когда дойдут руки)

Приоритеты не выставлены — расставим когда будет нужно действовать.

- [ ] Зафиксировать `prod_schema_2026-05-09.sql` в master (закоммитить)
- [ ] Восстановить dev-БД из `prod_schema_2026-05-09.sql`
- [ ] Обновить `tests/schema.sql` под актуальную прод-схему
- [ ] Привести `sqlScripts/` в соответствие с прод-схемой
- [ ] Удалить мёртвый код (после восстановления окружения и проверки что прод реально без него работает)
- [ ] Проверить и поправить `deploy.yml` (безопасность)
- [ ] Решить судьбу `BinanceCollectorWorker` (включить / удалить)
- [ ] Удалить устаревшие ветки `docker-refactor`, `test/ci`
- [ ] Решить судьбу `docker-compose.override.yml` (восстановить из бэкапа или нет)
- [ ] Восстановить работу с двумя файлами (`docker-compose.yml` + `docker-compose.prod.yml`) — сейчас `docker-compose.prod.yml` самодостаточен и override-паттерн сломан (2026-05-20)
- [ ] Удалить пустой `postgres-config/custom.conf`
- [ ] Убрать `ConfigureKestrel` из `DataManager/Program.cs` и `Worker/Program.cs` (см. п. 7)

---

## История ревизий

- **2026-05-09:** первичный анализ при переезде с Ubuntu на Windows + VirtualBox. Восстановление dev-окружения с нуля. Полная ревизия документации (см. `docs/INDEX.md`). Снят `pg_dump` с прода как baseline.