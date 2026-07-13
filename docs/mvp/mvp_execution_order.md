# План: завершение переноса initial load на прод

Перенос данных с dev на прод выполнен 2026-07-12: архивы (3652 ZIP + 475 CSV) перелиты в `binancecollector_bdc_data`, внешний 4TB-диск физически переставлен на GMKtec и примонтирован в `/mnt/ext` (fstab по UUID `25ddc534-13e6-479e-8392-a4487a975c80`), `docker-compose.prod.yml` переведён с named volume на bind mount `/mnt/ext/postgres_data` (коммит `88c14f9`). Проверено на проде: 7 таблиц + 24 партиции, `Trades` — partitioned table. DEV приведён к целевому состоянию: Postgres на docker volume `dev_postgres_data`, схема из baseline, без исторических данных. Дальше — то, что осталось.

## Текущее состояние

- **Прод:** `Trades` заполнены за 6 из ~19 месяцев (апр, авг–дек 2025), 566 ГБ. `Ohlcv_1min`/`Ohlcv_Features`/`Processing_Watermarks`/`HistoricalAudit_Watermarks` — пустые (Фазы 4–6 из `docs/INITIAL_DATA_LOAD.md` не начинались).
- Очередь `archive_import` приехала вместе с диском и дорабатывает сама — пересоздавать джобы не нужно. `UnpackArchiveAsync` резолвит путь через `IPathProvider` в момент выполнения; `ImportFromCsvAsync` несёт зашитый dev-путь, поэтому у `bdc_worker` тот же volume смонтирован дважды (`/opt/bdc_data` и `/home/lex/bdc_data`).
- Импорт crash-safe: CSV/ZIP удаляются только после успеха, вставка идемпотентна (`ON CONFLICT ... DO NOTHING`). Worker можно останавливать в любой момент.
- `partition-maintenance` (rotation) отключена в коде (`HangfireJobsService.cs`) — включать после завершения загрузки.
- Прод: GMKtec G2, Intel N150, Ubuntu 24.04, realtime-трафик не держит. SSH: `ssh -p 2237 lex@100.96.120.16`.
- **DEV:** база лёгкая (8.7 МБ) — только схема, 24 пустые партиции. Ротация партиций (`sp_rotate_trades_partition`, 13 месяцев) общая с продом, отдельная dev-версия не делалась. Осталось руками: `sudo rm -rf /mnt/ext/postgres_data` (70 МБ мусора) и убрать запись про диск из `/etc/fstab`.

## Шаг 1 — Доделать прод (руками, на сервере)

- Systemd drop-in, чтобы Docker дожидался монтирования диска (иначе при автозапуске гонка: `bdc_db` может стартовать раньше монтирования и создать пустую БД):
  ```bash
  sudo mkdir -p /etc/systemd/system/docker.service.d
  sudo tee /etc/systemd/system/docker.service.d/wait-for-ext.conf > /dev/null <<'EOF'
  [Unit]
  RequiresMountsFor=/mnt/ext
  EOF
  sudo systemctl daemon-reload
  ```
- `x-systemd.device-timeout=30` в записи `/etc/fstab` для `/mnt/ext`.
- Залить `prod-stop.sh` на сервер (`scp` в `/opt/BinanceCollector/docker/`) — CI скрипты не разносит.
- Проверить ребутом, что стек поднимается на реальной базе.

## Шаг 2 — Проверить вход в DataManager

Фикс схемы применён (`Program.cs`: в Production форсируется `https`, коммит `3f369b2`) — TLS снимает Cloudflare, Traefik перезаписывает `X-Forwarded-Proto`, из-за чего `redirect_uri` строился с `http`. **Проверить, что логин через B2C теперь проходит.**

Если нет — смотреть в Azure (App registrations → Authentication → Redirect URIs), есть ли там `https://datamanager.jahasim.com/signin-oidc`.

## Гигиена секретов

- `src/BinanceDataCollector.Worker/Properties/launchSettings.json` **отслеживается гитом** и содержит `RabbitMQ__Password` — вынести из репозитория (user-secrets / env).
- B2C client secret лежит только локально в `DataManager/Properties/launchSettings.json` (файл в `.gitignore`, в истории гита его нет — проверено `git log -S`). В репозиторий не утёк.

## Шаг 2 — Фазы 3–6 из `docs/INITIAL_DATA_LOAD.md`, на проде

1. **Фаза 3 (идёт):** дождаться, пока очередь `archive_import` разберётся сама. Мониторинг — Hangfire Dashboard.
2. **Фаза 4 (историческая агрегация):** PL/pgSQL-скрипт из `INITIAL_DATA_LOAD.md`, выполнить через psql/DBeaver на проде, когда Фаза 3 дойдёт до конца.
3. **Фаза 5:** SQL для заполнения `HistoricalAudit_Watermarks`.
4. **Фаза 6:** включить recurring jobs (`ohlcv-aggregator`, `feature-calculator`, `historical-audit`, `quick_audit`, `partition-maintenance`) — раскомментировать в `HangfireJobsService.cs`, задеплоить.

**Критерий завершения:** `archive_import` пуст, скрипт агрегации вывел `Done`, вотермарки проставлены, recurring jobs видны в Hangfire Dashboard.

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
