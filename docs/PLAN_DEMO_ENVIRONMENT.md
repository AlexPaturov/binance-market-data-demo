# План: demo-окружение

Рабочий план. Живёт до реализации, после закрытия последней фазы удаляется, а результат описывается в `dev/` и README.

## Цель

`git clone` → одна команда → `http://localhost:7002` с наполненной БД. Единственная внешняя зависимость — Docker. Одна и та же команда на Linux, Windows и macOS.

```bash
docker compose -f docker/compose/docker-compose.demo.yml up -d
```

## Принятые решения

**Аутентификация — переключатель `Authentication:Mode`: `B2C` (по умолчанию) | `Demo`.**
В режиме `Demo` вместо OpenID Connect регистрируется локальная cookie-схема и страница выбора роли Viewer/Operator/Admin; куки — `SameAsRequest`/`Lax`. `FallbackPolicy` и три политики (`DataManager/Program.cs:115-126`) действуют без изменений: демо проходит ту же авторизацию, что прод, включая `AdminHangfireAuthorizationFilter`.

**Seed — срез реальных данных на 1–2 символа за ~месяц.**
`TrackedSymbols`, `Trades`, `Ohlcv_1min`, `Ohlcv_Features`, `ArchiveImportLog`, `MonthSeal` — чтобы в демо были видны и закрытый месяц, и эвакуация на холодный tablespace. Формат `COPY` + gzip; генератор из живой БД по образцу `docker/postgres/regen-schema.sh`; загрузка отдельным init-шагом только в demo-compose. Схема — штатный `docker/postgres/init/02_baseline.sql`.

## Исходное состояние

Проверено по коду на 2026-07-20; это то, что фазы 0–2 приводят к переносимому виду.

| Что | Где | Что делает сейчас |
|---|---|---|
| Тома dev-стека | `docker-compose.dev.yml` | `seq_data`, `rabbitmq_data`, `letsencrypt_data`, `bdc_data` объявлены `external: true` с именами `binancecollector_*` и требуют предварительного создания |
| Переменные окружения | `.gitignore:383` | `.env` игнорируется; значения `POSTGRES_*`, `RABBITMQ_*`, `SEQ_*` берутся из него |
| Вход в DataManager | `DataManager/Program.cs:100-119` | глобальный `RequireAuthenticatedUser`, challenge в Azure B2C; куки `SecurePolicy.Always` + `SameSite=None` рассчитаны на HTTPS за Cloudflare |
| Коллекторы Binance | `Worker/Program.cs:153,157` | `BinanceCollectorWorker` и `OrderBookCollectorWorker` регистрируются при любом старте |
| Наполнение данными | страница `/Archive` | скачивание и импорт архивов Binance |
| Пути к архивам | `appsettings.Development.json`, `Domain/DTOs/ArchivesSettings.cs:13` | `/home/lex/bdc_data` и дефолт `/opt/bdc_data` |
| Окончания строк | `.gitattributes` | `* text=auto`: при checkout на Windows `.sh` получают CRLF |
| Холодный tablespace | `docker-compose.dev.yml` | bind-mount `./dev_cold:/mnt/pg_tablespaces`, Postgres делает `chown` этой директории на старте |

## Фазы

### Фаза 0 — стек без привязки к машине

- [ ] `docker/compose/.env.example` с плейсхолдерами
- [ ] Тома в `docker-compose.dev.yml` создаются самим compose
- [ ] `ArchivesSettings:BasePath` задаётся переменной окружения
- [ ] `*.sh text eol=lf` в `.gitattributes`
- [ ] `dev_cold` переведён на именованный том

**Проверка:** удалить тома `binancecollector_*`, поднять dev с нуля.

### Фаза 1 — demo-аутентификация

- [ ] Переключатель `Authentication:Mode`
- [ ] Cookie-схема и страница выбора роли для режима `Demo`
- [ ] Настройки кук зависят от режима
- [ ] Кейс demo-принципала в `PolicyAuthorizationTests`

**Проверка:** вход всеми тремя ролями; Operator не получает админских действий.

### Фаза 2 — автономность от Binance

- [ ] Флаг `Collectors:Enabled` (по умолчанию `true`)
- [ ] При `false` коллекторы и джобы, обращающиеся к Binance API, не регистрируются

Событийные консьюмеры (`OhlcvAggregationService`, `FeatureCalculationService`) и `PartitionMaintenanceWorker` работают в демо — это содержательная часть конвейера.

**Проверка:** поднять demo без сети; Worker жив, консьюмеры слушают, логи чистые.

### Фаза 3 — seed

- [ ] Скрипт генерации среза из живой БД
- [ ] Артефакт в репозитории
- [ ] Init-шаг загрузки в demo-compose

Объём замеряется до фиксации: если срез `Trades` выходит за ~50 МБ — сузить окно до двух недель, сохранив состав таблиц.

**Проверка:** чистый том → страницы DataManager наполнены, `MonthSeal` проставлен, Data Quality отрабатывает.

### Фаза 4 — одна команда и три ОС

- [ ] `docker/compose/docker-compose.demo.yml`
- [ ] Раздел в README
- [ ] Образы под arm64: `edoburu/pgbouncer`, `datalust/seq`, сборка `postgres:16 + postgresql-16-cron`

**Проверка:** Linux, Windows, macOS.

## Границы

- Фазы 0–2 меняют `Program.cs` обоих приложений. Каждое изменение закрыто флагом, значение по умолчанию совпадает с текущим поведением; прод-конфигурация ничего не переопределяет.
- `.env.example` содержит только плейсхолдеры.
- Проверка на Windows и macOS выполняется на стороне владельца проекта.
