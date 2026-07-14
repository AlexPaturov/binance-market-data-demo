# Технический долг

Что известно и не сделано. Только живые пункты — закрытое отсюда убирается.

---

## 1. Секреты

### Пароль Seq захардкожен в `deploy.yml`

```yaml
SEQ_ADMIN_USER=lex
SEQ_ADMIN_PASS=lex
```

Должно быть в GitHub Secrets.

### Пароль Seq остался в истории гита

Файл `src/BinanceDataCollector.Worker/docs/miscelanious.md` содержал логин и пароль от Seq открытым текстом. Файл удалён, но **в истории он остаётся** (коммит `402e0e2`). Пароль надо **сменить** — удаление файла его не отзывает.

### `RabbitMQ__Password` в отслеживаемом файле

`src/BinanceDataCollector.Worker/Properties/launchSettings.json` лежит в гите и содержит пароль RabbitMQ. Вынести в user-secrets или env, а файл — в `.gitignore` (как уже сделано для DataManager).

---

## 2. Дублирование SQL проверок качества

Разрывы `TradeId`, невалидные цены и 5σ-выбросы считаются **двумя разными запросами** в одном `DataQualityRepository`:

- `CheckSymbolMonthAsync` → `DataQualityReports` (карта покрытия по месяцам),
- `RunTradesChecksAsync` → `DataQualityFindings` (журнал за произвольный период).

Обе таблицы оставлены осознанно — они отвечают на разные вопросы. Но логика подсчёта одних и тех же дефектов продублирована и может разъехаться.

**Решение:** свести к одному запросу — `CheckSymbolMonthAsync` считает тем же SQL и сворачивает результат в формат отчёта.

---

## 3. `ConfigureKestrel` конфликтует с `ASPNETCORE_URLS`

В обоих `Program.cs` порт задаётся явно через `ConfigureKestrel(options => options.ListenAnyIP(...))`. Kestrel при этом игнорирует `applicationUrl` из `launchSettings.json` и `ASPNETCORE_URLS` из compose, выдавая warning при каждом старте.

Убрать `ConfigureKestrel`: в dev порт берётся из `launchSettings`, в проде — из `ASPNETCORE_URLS` (уже настроено).

---

## 4. Hangfire Dashboard в DataManager

Защита дашборда полагается на `AdminHangfireAuthorizationFilter` и на Traefik/Cloudflare. Стоит проверить, что при прямом обращении в обход Traefik он действительно закрыт.

---

## 5. Отмена импорта логируется как ошибка

При штатной остановке Worker'а активный `CsvImportWorker.ImportFromCsvAsync` получает cancellation token и пишет `[ERR] Error importing from file ...`. По смыслу это контролируемое завершение, а не сбой импорта. Обработать `OperationCanceledException` отдельно и логировать как `Information`.

---

## 6. Второй mount `bdc_data` в `bdc_worker`

`docker-compose.prod.yml` монтирует volume дважды — в `/opt/bdc_data` и в `/home/lex/bdc_data`. Второй путь был нужен, чтобы отработали Hangfire-джобы импорта CSV, поставленные ещё на dev-машине: в их аргументах зашит абсолютный dev-путь.

Очередь тех джоб давно вычищена. Второй mount можно убрать.

---

## 7. Проверка границы ретенции в воркерах

Барьер против закачки удалённых месяцев стоит на уровне БД: `sp_ensure_month_partitions` отказывается создавать партицию ниже границы ретенции. Этого достаточно для корректности, но неаккуратно: архив за такой месяц сначала скачается (гигабайты трафика) и только потом упрётся в ошибку вставки.

Воркеры (`HistoricalAuditorWorker`, импорт архивов) должны проверять границу **до** скачивания.

---

## 8. Dev/Prod синхронизация БД

Для разработки индикаторов полезны реальные тиковые данные, но полное зеркало прода на dev-машину не влезает.

**Идея:** rolling window — держать на dev последние 3 месяца (~150–200 ГБ), подтягивать дельту с прода через Tailscale, старое удалять.

Не реализовано и, возможно, уже не нужно: realtime-сбор работает и на dev, так что данные накапливаются сами.
