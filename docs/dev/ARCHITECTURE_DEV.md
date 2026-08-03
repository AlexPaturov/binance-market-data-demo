# Документация: ARCHITECTURE_DEV — DEV-окружение

Этот документ описывает физическую и сетевую модель **dev-окружения** проекта `BinanceDataCollector`.

Цель: одной страницей показать, **где что крутится**, **по каким адресам сервисы доступны** и **как .NET-приложения цепляются к инфраструктуре** при разработке.

> **DEV ≠ зеркало прода.** В DEV нет Cloudflare Tunnel, Traefik, Let's Encrypt и доменов `*.jahasim.com`. Инфраструктура (Postgres, PgBouncer, RabbitMQ, Seq) живёт **в Docker на Ubuntu-хосте**, а Worker и DataManager запускаются **из Rider как обычные .NET-процессы**.

---

## 1. Физическая инфраструктура

```
+----------------------------------------------------------+
|  Ubuntu host (рабочая машина разработчика)               |
|                                                          |
|   +---------------------------+                          |
|   |  Rider                    |                          |
|   |   - bdc_worker      :7001 |   .NET-процессы на хосте|
|   |   - bdc_datamanager :7002 |                          |
|   +-------------┬-------------+                          |
|                 │ TCP localhost                          |
|                 ▼                                        |
|   +--------------------------------------------------+   |
|   |  Docker (на том же хосте)                        |   |
|   |                                                  |   |
|   |   - bdc_db (Postgres 16 + pg_cron)  :5432         |   |
|   |   - bdc_pgbouncer          :6432                 |   |
|   |   - bdc_rabbitmq           :5672 / :15672        |   |
|   |   - bdc_seq                :5341                 |   |
|   |                                                  |   |
|   |  Данные Postgres: docker volume                  |   |
|   |  dev_postgres_data                               |   |
|   +--------------------------------------------------+   |
|                                                          |
|  Данные Worker (архивы и CSV):                           |
|    ~/bdc_data/Trades/Downloaded                          |
|    ~/bdc_data/Trades/Unpacked                            |
|    (BasePath в ArchivesSettings, appsettings.Dev.json)   |
+----------------------------------------------------------+
```

- **Хост:** Ubuntu. На нём Rider, .NET-приложения и Docker.
- **Docker** установлен прямо на хосте. VM нет.
- **Worker / DataManager** в DEV **в Docker не запускаются**. Они стартуют из Rider.

---

## 2. Адреса сервисов

Всё — на `localhost`. Никаких IP виртуалок.

| Сервис             | Адрес для приложений       | Назначение                          |
|--------------------|----------------------------|-------------------------------------|
| Postgres           | `localhost:5432`           | прямой доступ (DBeaver)             |
| PgBouncer          | `localhost:6432`           | точка входа для Worker и DataManager|
| RabbitMQ (AMQP)    | `localhost:5672`           | брокер сообщений                    |
| RabbitMQ (UI)      | `http://localhost:15672`   | management dashboard                |
| Seq                | `http://localhost:5341`    | логи (Serilog ingest)               |
| Worker (HTTP)      | `http://localhost:7001`    | запускается из Rider                |
| Hangfire Dashboard | `http://localhost:7001/hangfire` | джобы                         |
| DataManager (HTTP) | `http://localhost:7002`    | запускается из Rider                |

---

## 3. Базы данных

Образ БД — тот же, что на проде: собственный `bdc/postgres-cron:16` из `docker/postgres/Dockerfile` (postgres:16 + `pg_cron`). Коллация — `C.UTF-8`, как на проде: один образ, один init, ноль дрейфа окружений (см. [ADR 0011](../adr/0011-hot-cold-tiering-pg-cron.md)). Тиринг на деве номинальный — `cold` это локальная папка `dev_cold` на том же диске.

В контейнере две независимых базы (как в проде):

- **`market_analytics`** — основная бизнес-БД (трейды, OHLCV, tracked symbols). Создаётся через `POSTGRES_DB`.
- **`market_analytics_jobs`** — служебная БД для Hangfire.

Схема применяется **автоматически** при первом старте на пустом volume — init-скрипты из `docker/postgres/init/` (`docker-entrypoint-initdb.d`), по порядку:

| Скрипт | Что делает |
| :--- | :--- |
| `01_create_jobs_db.sql` | создаёт `market_analytics_jobs` |
| `02_baseline.sql` | полная схема данных: партиционированная `Trades`, свечи, фичи, очередь `DirtyMinutes`, журнал `ArchiveImportLog`, печати `MonthSeal`, все процедуры + журнал `schema_migrations` (сид свёрнутых миграций). Канонический `pg_dump`, генерируется `regen-schema.sh` ([ADR 0013](../adr/0013-schema-baseline-and-migration-automation.md)) |
| `03_hangfire_schema.sql` | схема Hangfire (дамп с прода — приложение её не создаёт, `PrepareSchemaIfNecessary=false`) |
| `04_tablespace_and_cron.sql` | tablespace `cold`, расширение `pg_cron`, расписание эвакуации |

> Скрипты выполняются **только при пустом data dir** (первый запуск). На существующих данных init не запускается — новую миграцию на живой дев накатывает раннер `docker/postgres/migrate.sh` (идемпотентно, по журналу `schema_migrations`) либо пересоздание volume.

Первичного наката схемы руками не требуется. Как менять схему — `docker/postgres/README.md`.

---

## 4. Хранение данных Docker-контейнеров

| Контейнер   | Где хранятся данные               | Тип монтирования |
|-------------|-----------------------------------|------------------|
| Postgres    | named volume `dev_postgres_data`  | named volume     |
| Seq         | named volume `bdc_seq_data`       | named volume     |
| RabbitMQ    | named volume `bdc_rabbitmq_data`  | named volume     |

DEV-база — локальная и лёгкая: только схема, без исторических данных (подробности init — раздел 3). Плюс локальная папка `dev_cold` (bind-mount под tablespace `cold`) — рантайм-данные, в `.gitignore`.

Исторические данные (сотни ГБ) живут на проде: горячие месяцы на внутреннем SSD, закрытые — на внешнем HDD ([ADR 0011](../adr/0011-hot-cold-tiering-pg-cron.md)).

> Чтобы пересоздать DEV-базу с нуля: остановить `bdc_db`, `docker volume rm compose_dev_postgres_data`, при смене образа — `docker compose -f docker-compose.dev.yml build bdc_db`, затем up. Схема (все init-скрипты) накатится заново.

---

## 5. Connection strings (`launchSettings.json`)

Файл **не в git** (`.gitignore`). Создаётся вручную по шаблону:

**Worker** (`src/BinanceDataCollector.Worker/Properties/launchSettings.json`):
```json
{
  "profiles": {
    "WorkerProf": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "http://localhost:7001",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_UTF8_CONSOLE": "true",
        "ConnectionStrings__DefaultConnection": "Host=localhost;Port=6432;Database=market_analytics;Username=bindatacoll;Password=<password>",
        "ConnectionStrings__DirectConnection": "Host=localhost;Port=5432;Database=market_analytics;Username=bindatacoll;Password=<password>",
        "ConnectionStrings__HangfireConnection": "Host=localhost;Port=6432;Database=market_analytics_jobs;Username=bindatacoll;Password=<password>",
        "RabbitMQ__HostName": "localhost",
        "RabbitMQ__UserName": "guest",
        "RabbitMQ__Password": "guest",
        "RabbitMQ__Port": "5672",
        "Serilog__WriteTo__1__Name": "Seq",
        "Serilog__WriteTo__1__Args__serverUrl": "http://localhost:5341"
      }
    }
  }
}
```

`DirectConnection` отличается от `DefaultConnection` только портом: `5432` (Postgres напрямую) вместо `6432` (PgBouncer). Она **обязательна** — событийные слушатели (`OhlcvAggregationService`, `FeatureCalculationService`) на старте падают без неё: `LISTEN` не переживает transaction-режим PgBouncer, поэтому слушатель ходит в базу мимо пула ([ADR 0010](../adr/0010-event-driven-aggregation.md)). Рабочие запросы разбора очереди по-прежнему идут через `DefaultConnection`.

**DataManager** — аналогично, профиль `DataManagerProf`, `applicationUrl` — `http://localhost:7002`. `DirectConnection` ему не нужна — слушатели хостит только Worker.

---

## 6. Запуск инфраструктуры

Из корня проекта:

```bash
./docker/dev-start.sh
```

Скрипт создаёт `~/bdc_data/` поддиректории если их нет, затем поднимает инфраструктуру. Безопасно запускать повторно.

Остановка:
```bash
./docker/dev-stop.sh
```

После запуска инфраструктуры — запустить Worker и DataManager из Rider (Compound run configuration `Dev (Worker + DataManager)`).

---

## 7. Demo-окружение

Рядом с dev-режимом (инфраструктура в Docker + приложения из Rider) существует **demo** — самодостаточный контейнерный стек с предзагруженными данными, для запуска на чужой машине без сети и без Azure B2C. Все сервисы, включая Worker и DataManager, идут в Docker через `docker-compose.demo.yml`; данные приходят из seed-среза (`docker/postgres/seed/`, BTCUSDT за февраль 2026), загружаемого init-скриптом `05_seed.sh` на чистом томе. Запуск по системам — `docs/dev/DEMO_LINUX.md`, `DEMO_WINDOWS.md`, `DEMO_MACOS.md`; чек-лист приёмки — `DEMO_ACCEPTANCE.md`.

Поведение задают три конфиг-флага (значения по умолчанию = боевой режим, прод не затронут):

| Флаг | Demo | По умолчанию | Эффект |
| :--- | :--- | :--- | :--- |
| `ASPNETCORE_ENVIRONMENT` | `Demo` | `Production` / `Development` | Отдельное окружение. `IsDemo()` (`DataManager/Common/HostEnvironmentExtensions.cs`) отключает прод-специфику: форс https-схемы за прокси, HSTS, каталог DataProtection-ключей `/opt/bdc_data`. |
| `Authentication:Mode` | `Demo` | `B2C` | В `Demo` вместо Azure B2C — локальная cookie-схема и страница `/demo-login` с выбором роли Viewer/Operator/Admin (`DemoAuthController`). Политики авторизации те же, что в проде: роль кладётся в claim `ClaimTypes.Role`, `FallbackPolicy` и `RequireRole` действуют без изменений. |
| `Collectors:Enabled` | `false` | `true` | При `false` не регистрируются `BinanceCollectorWorker`/`OrderBookCollectorWorker` и Hangfire-джобы, обращающиеся к Binance API (`update-symbols`, аудиторы). Событийный конвейер (агрегация свечей, расчёт фич) и обслуживание партиций работают поверх seed-данных. |

---

## 8. Что нельзя делать в DEV

- Использовать `docker-compose.prod.yml` — он рассчитан на Traefik / Cloudflare Tunnel и сломает локальный запуск.
- Использовать `docker-compose.dev.yml` для запуска инфраструктуры из IDE — он поднимает Worker и DataManager **в Docker**, что конфликтует с запуском из Rider.
- Считать, что Worker/DataManager доступны изнутри Docker-контейнеров по `localhost` — они работают на хосте, а не внутри сети Docker. Контейнеры обращаются к хосту через `host.docker.internal` (если нужно).
- Хранить ценные данные только в dev-volumes: `docker volume prune` или пересоздание контейнеров уничтожит данные. Dev-БД восстанавливается из схемы + импорта.
