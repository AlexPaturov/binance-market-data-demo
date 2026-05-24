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
|   |   - bdc_db (Postgres 16)   :5432                 |   |
|   |   - bdc_pgbouncer          :6432                 |   |
|   |   - bdc_rabbitmq           :5672 / :15672        |   |
|   |   - bdc_seq                :5341                 |   |
|   |                                                  |   |
|   |  Данные Postgres: /mnt/ext/postgres_data        |   |
|   |  (bind mount → внешний диск 4TB, /dev/sda1)     |   |
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

В Postgres-контейнере две независимых базы (как в проде):

- **`market_analytics`** — основная бизнес-БД (трейды, OHLCV, tracked symbols). Создаётся через `POSTGRES_DB` в docker-compose.
- **`market_analytics_jobs`** — служебная БД для Hangfire. Создаётся автоматически при первом старте Postgres через init-скрипт `docker/postgres/init/01_create_jobs_db.sql`.

> Init-скрипт выполняется **только при пустом data dir** (первый запуск). На существующих данных — не запускается.

После первого поднятия контейнера нужно применить схему:
```bash
docker exec -i bdc_db psql -U bindatacoll -d market_analytics < sqlScripts/prod_schema_2026-05-09.sql
```

---

## 4. Хранение данных Docker-контейнеров

| Контейнер   | Где хранятся данные              | Тип монтирования |
|-------------|----------------------------------|------------------|
| Postgres    | `/mnt/ext/postgres_data`         | bind mount       |
| Seq         | named volume `bdc_seq_data`      | named volume     |
| RabbitMQ    | named volume `bdc_rabbitmq_data` | named volume     |

Postgres хранится на внешнем диске `/dev/sda1` (4TB, ext4, UUID `25ddc534-13e6-479e-8392-a4487a975c80`), примонтированном в `/mnt/ext` через `/etc/fstab` с флагом `nofail`. Диск содержит партиционированную таблицу `Trades` с историческими данными Jan 2025–Dec 2026 (24 партиции).

> Если диск не примонтирован при старте Docker — `nofail` не даст системе зависнуть, но Postgres не запустится (пустая директория). Перед `dev-start.sh` убедись что `/mnt/ext` примонтирован: `df -h /mnt/ext`.

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

**DataManager** — аналогично, профиль `DataManagerProf`, `applicationUrl` — `http://localhost:7002`.

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

## 7. Что нельзя делать в DEV

- Использовать `docker-compose.prod.yml` — он рассчитан на Traefik / Cloudflare Tunnel и сломает локальный запуск.
- Использовать `docker-compose.dev.yml` для запуска инфраструктуры из IDE — он поднимает Worker и DataManager **в Docker**, что конфликтует с запуском из Rider.
- Считать, что Worker/DataManager доступны изнутри Docker-контейнеров по `localhost` — они работают на хосте, а не внутри сети Docker. Контейнеры обращаются к хосту через `host.docker.internal` (если нужно).
- Хранить ценные данные только в dev-volumes: `docker volume prune` или пересоздание контейнеров уничтожит данные. Dev-БД восстанавливается из схемы + импорта.
