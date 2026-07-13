# План: завершение переноса initial load на прод

Перенос с dev на прод завершён 2026-07-12/13: архивы перелиты, внешний 4TB-диск переставлен на GMKtec, `docker-compose.prod.yml` переведён на bind mount `/mnt/ext/postgres_data`, автозапуск защищён от гонки с монтированием (systemd drop-in `RequiresMountsFor=/mnt/ext`), скрипты `prod-start.sh`/`prod-stop.sh` на сервере. DEV приведён к целевому состоянию: Postgres на docker volume `dev_postgres_data`, схема из baseline, без исторических данных. Дальше — то, что осталось.

## Текущее состояние

- **Прод:** `Trades` заполнены за 6 из ~19 месяцев (апр, авг–дек 2025), 566 ГБ и растут. `Ohlcv_1min`/`Ohlcv_Features`/`Processing_Watermarks`/`HistoricalAudit_Watermarks` — пустые (Фазы 4–6 из `docs/INITIAL_DATA_LOAD.md` не начинались).
- Очередь `archive_import` дорабатывает сама. `ImportFromCsvAsync` несёт зашитый dev-путь, поэтому у `bdc_worker` тот же volume смонтирован дважды (`/opt/bdc_data` и `/home/lex/bdc_data`); второй mount можно убрать, когда очередь опустеет.
- Импорт crash-safe: CSV/ZIP удаляются только после успеха, вставка идемпотентна (`ON CONFLICT ... DO NOTHING`). Worker можно останавливать в любой момент.
- `partition-maintenance` (rotation) отключена в коде (`HangfireJobsService.cs`) — включать после завершения загрузки.
- Железо: GMKtec G2, Intel N150, Ubuntu 24.04, realtime-трафик не держит. SSH: `ssh -p 2237 lex@100.96.120.16`.
- **DEV:** база лёгкая (8.7 МБ) — только схема, 24 пустые партиции. Ротация партиций (`sp_rotate_trades_partition`, 13 месяцев) общая с продом, отдельная dev-версия не делалась.

## Шаг 1 — Проверить вход в DataManager

Фикс схемы применён (`Program.cs`: в Production форсируется `https`, коммит `3f369b2`) — TLS снимает Cloudflare, Traefik перезаписывает `X-Forwarded-Proto`, из-за чего `redirect_uri` строился с `http`. **Проверить, что логин через B2C теперь проходит.**

Если нет — смотреть в Azure (App registrations → Authentication → Redirect URIs), есть ли там `https://datamanager.jahasim.com/signin-oidc`.

## Шаг 2 — Панель Data Quality: смержить и накатить

**Код готов, лежит в ветке `feature/data-quality-panel` (коммит `860d11c`), в `master` не влит.** 34 теста зелёные, включая Testcontainers-тесты, которые сажают битые данные и проверяют, что каждая проверка их находит.

Что в ветке:
- Таблица `"DataQualityFindings"` (группа, тип проверки, символ, период, severity, счётчик, детали в JSONB).
- 18 проверок в 4 группах: сырые тики / свечи / индикаторы / пайплайн (watermarks).
- `DataQualityCheckWorker` — Hangfire-джоба **без расписания**, только по кнопке. Старый `DataQualityWorker` с `Cron.Never()` удалён, регистрация `data-quality-check` вычищена.
- Страница `/DataQuality`: фильтры (символ, период, группы), живой журнал через SignalR, таблица находок с фильтрами по группе/статусу/символу. Запуск — `Operator`, просмотр — `Viewer`.
- Диапазон одной проверки жёстко ограничен **31 днём** (проверяется в JS, в контроллере и в репозитории) — иначе любая проверка вырождается в полный скан сотен ГБ.

**Осталось:**
1. Смержить ветку в `master` (пойдёт деплой — учтите, что на проде идёт импорт).
2. **Накатить миграцию на прод-базу вручную** — в baseline таблица есть, но baseline применяется только на чистом томе:
   ```bash
   docker exec -i bdc_db psql -U bindatacoll -d market_analytics < docker/postgres/migrations/001_data_quality_findings.sql
   ```
3. Открыть `/DataQuality` и проверить в браузере (в коде не проверялось — нужен вход через B2C).

**Осознанно пропущено:** `CHECK`-констрейнты на инварианты (`High >= Low`, `RSI` в `[0,100]`, кратность `OpenTime`). Это размен «предотвращение → обнаружение»: битая строка попадёт в базу и будет найдена только при запуске проверки.

## Шаг 3 — Фазы 3–6 из `docs/INITIAL_DATA_LOAD.md`, на проде

1. **Фаза 3 (идёт):** дождаться, пока очередь `archive_import` разберётся сама. Мониторинг — Hangfire Dashboard.
2. **Фаза 4 (историческая агрегация):** PL/pgSQL-скрипт из `INITIAL_DATA_LOAD.md`, выполнить через psql/DBeaver на проде, когда Фаза 3 дойдёт до конца.
3. **Фаза 5:** SQL для заполнения `HistoricalAudit_Watermarks`.
4. **Фаза 6:** включить recurring jobs (`ohlcv-aggregator`, `feature-calculator`, `historical-audit`, `quick_audit`, `partition-maintenance`) — раскомментировать в `HangfireJobsService.cs`, задеплоить.
5. Прогнать полную проверку качества данных новой панелью — это и есть «проверка целостности прод-базы» из `docs/TODO_POST_INITIAL_LOAD.md`.

**Критерий завершения:** `archive_import` пуст, скрипт агрегации вывел `Done`, вотермарки проставлены, recurring jobs видны в Hangfire Dashboard.

## Гигиена секретов (отложено)

- `src/BinanceDataCollector.Worker/Properties/launchSettings.json` **отслеживается гитом** и содержит `RabbitMQ__Password` — вынести из репозитория (user-secrets / env).
- B2C client secret лежит только локально в `DataManager/Properties/launchSettings.json` (файл в `.gitignore`, в истории гита его нет — проверено `git log -S`). В репозиторий не утёк.

---

# Сценарии порчи данных

Каталог из 18 проверок, разложенных по 4 группам, **реализован** в ветке
`feature/data-quality-panel` (см. Шаг 2) — держать его отдельным списком в этом
документе больше нет смысла. Источник истины теперь код:

- Группы и пороги — `src/BinanceDataCollector.Application/Common/DataQualityChecks.cs`
- Сами проверки (SQL) — `DataQualityRepository.Run*ChecksAsync`
- Что каждая проверка ловит — тесты `DataQualityRepositoryTests`: они сажают
  заведомо битые данные и проверяют, что проверка их находит.

**Не реализовано осознанно:**

- `CHECK`-констрейнты на дешёвые инварианты (`High >= Low`, `Open`/`Close` внутри
  диапазона, `OpenTime % 60000 = 0`, `RSI` в `[0, 100]`, `Price > 0`). Они ловили бы
  порчу в момент записи, а не при запуске проверки. Решение отложено — накатывать
  их дешевле, пока `Ohlcv_1min` пуста, то есть до Фазы 4.
- Сверка агрегата свечи со свежим пересчётом из `"Trades"` — тяжёлая проверка,
  требует полного пересчёта окна.
- Уточнение порога выбросов (сейчас фиксированные 5σ; MAD устойчивее к «толстым
  хвостам» крипторынка).
