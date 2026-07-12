# Документация: 04 — Развёртывание и CI/CD (prod)

Документ описывает фактическое поведение CI/CD-пайплайна и состав того, что
разворачивается на прод-сервере `analserver` (GMKtec G2). Источник истины —
файлы `.github/workflows/deploy.yml`, `docker/Dockerfile`,
`docker/compose/docker-compose.yml` и `docker/compose/docker-compose.prod.yml`.

> Связанные документы:
> - `docs/prod/05_setup.md` — первичная настройка сервера и self-hosted runner.
> - `docs/prod/ARCHITECTURE_PROD.md` — железо, сеть и общий состав сервисов.
> - `docs/Server_Network_Config.md` — детали по сетям, доменам, Cloudflare Tunnel.
> - `docs/TECH_DEBT.md` — известные проблемы пайплайна и инфраструктуры.

---

## 1. Обзор пайплайна

При каждом изменении в `master` стартует workflow `Build and Deploy to
Production` (`.github/workflows/deploy.yml`), один job `build-and-deploy`
делает всю цепочку: checkout → сборка двух образов → push в GHCR → деплой
на сервер.

- **Триггер:** `push` **или** `pull_request` в ветку `master`.
  ⚠️ `pull_request` означает, что любой PR в `master` тоже инициирует
  деплой на прод. См. `docs/TECH_DEBT.md` (раздел 1).
- **Среда выполнения:** **self-hosted runner** установлен прямо на
  прод-сервере `analserver`. Это решает проблему с серым IP и ускоряет
  push в локальный Docker. Регистрация и запуск через `./config.sh` и
  `./svc.sh install/start` — см. `docs/prod/05_setup.md`, раздел 5.
- **Образов два**, оба собираются параллельно из одного multi-stage
  `docker/Dockerfile`:
  - `bdc_worker` — фоновые воркеры + Hangfire-серверы;
  - `bdc_datamanager` — ASP.NET MVC backoffice/UI с Azure AD B2C-логином.
- **Версионирование:** `APP_VERSION = ${GITHUB_SHA::7}` — первые 7
  символов SHA коммита. Тэг `:latest` **не ставится**: каждый push
  создаёт уникальный иммутабельный тэг.

---

## 2. Workflow `deploy.yml`

Все шаги ниже выполняются последовательно в одном job на self-hosted
runner'е.

1. **Checkout** — `actions/checkout@v4`.
2. **Calculate APP_VERSION** — `echo "APP_VERSION=${GITHUB_SHA::7}" >> $GITHUB_OUTPUT`.
3. **List files for debugging** — `ls -R`. Засоряет лог, остался от
   первоначальной отладки. Зафиксировано в `TECH_DEBT.md`.
4. **Set up Docker image names** — формирует пути в GHCR в нижнем регистре:
   - `ghcr.io/alexpaturov/binancedatacollector/worker`
   - `ghcr.io/alexpaturov/binancedatacollector/datamanager`
5. **Login to GHCR** — `docker/login-action@v3`. Username = `${{ github.actor }}`,
   password = секрет `DOCKER_PASSWORD` (PAT с правом `write:packages`).
6. **Build and push Worker** — `docker build --file docker/Dockerfile --target worker`,
   тэг `<image>:<APP_VERSION>`, затем `docker push`.
7. **Build and push DataManager** — то же самое с `--target datamanager`.
8. **Deploy to Server** — выполняется на самом сервере (тот же runner):
   - копируется `docker/compose/docker-compose.prod.yml` →
     `/opt/BinanceCollector/docker/compose/docker-compose.prod.yml`;
   - заходит в `/opt/BinanceCollector/docker/compose`;
   - **генерирует `.env`** heredoc'ом из секретов и захардкоженных
     значений (см. раздел 4);
   - выводит `cat .env | sed 's/PASSWORD=.*/PASSWORD=***HIDDEN***/'` —
     ⚠️ маскирует только `PASSWORD=`, остальные секреты
     (`CLOUDFLARE_TUNNEL_TOKEN`, `AUTH_B2C_CLIENT_SECRET`) попадают в лог
     открытым текстом. Зафиксировано в `TECH_DEBT.md`;
   - идемпотентно создаёт external volumes (`docker volume create … || true`);
   - идемпотентно создаёт external networks (`binancecollector_web`,
     `internal_network`);
   - **`docker compose pull && docker compose up -d`** — без явных
     `-f` флагов. ⚠️ Поведение зависит от того, что лежит в
     `/opt/BinanceCollector/docker/compose/`. Зафиксировано в
     `TECH_DEBT.md`.

В пайплайне **нет шага `dotnet test`**. Тесты в репо есть, но в CI не
запускаются (см. `TECH_DEBT.md`).

---

## 3. Что собирается

### Prod-stages в `docker/Dockerfile`

| Stage          | Базовый образ                                        | Что делает                                                                                                                            |
|----------------|------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------|
| `base-build`   | `mcr.microsoft.com/dotnet/sdk:8.0`                   | Восстановление зависимостей (`dotnet restore`) + копирование исходников всех проектов решения.                                        |
| `build-worker` | `base-build`                                         | `dotnet publish` для `BinanceDataCollector.Worker` → `/app/worker`.                                                                    |
| `build-datamanager` | `base-build`                                    | `dotnet publish` для `BinanceDataCollector.DataManager` → `/app/datamanager`.                                                          |
| **`worker`** *(target prod)* | `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` | Финальный образ Worker. ENTRYPOINT `dotnet BinanceDataCollector.Worker.dll`. Содержит все фоновые воркеры (Symbol/Trades/Aggregator/Auditor) + Hangfire-серверы (`PriorityServer`, `BackgroundServer`). |
| **`datamanager`** *(target prod)* | `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` | Финальный образ DataManager. ENTRYPOINT `dotnet BinanceDataCollector.DataManager.dll`. ASP.NET MVC + SignalR backoffice/UI. Аутентификация через Azure AD B2C. |

### Dev-stages (только для `docker-compose.dev.yml`)

| Stage              | База                                | Особенности                                                                                                                |
|--------------------|-------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| `worker-dev`       | `mcr.microsoft.com/dotnet/sdk:8.0`  | `dotnet watch run` для Worker, `--urls=http://0.0.0.0:7001`, `--diagnostic-port 5600`. `DOTNET_USE_POLLING_FILE_WATCHER=1`. |
| `datamanager-dev`  | `mcr.microsoft.com/dotnet/sdk:8.0`  | `dotnet watch run` для DataManager, `--urls=http://0.0.0.0:7002`, `--diagnostic-port 5601`.                                 |

В прод-пайплайне dev-stages не собираются — workflow явно указывает
`--target worker` и `--target datamanager`.

---

## 4. GitHub Secrets и захардкоженные значения

### Секреты, которые читает workflow

| Secret                    | Куда попадает в `.env` / зачем                                                                |
|---------------------------|-----------------------------------------------------------------------------------------------|
| `DOCKER_PASSWORD`         | Login в GHCR (PAT с `write:packages`).                                                        |
| `DB_USER`                 | `POSTGRES_USER`.                                                                              |
| `DB_PASSWORD`             | `POSTGRES_PASSWORD`.                                                                          |
| `ACME_EMAIL`              | `ACME_EMAIL` — Let's Encrypt в Traefik.                                                       |
| `RABBITMQ_USER`           | `RABBITMQ_USER`.                                                                              |
| `RABBITMQ_PASS`           | `RABBITMQ_PASSWORD`. ⚠️ имя секрета `_PASS`, а переменная в `.env` — `_PASSWORD`. Не путать.   |
| `CLOUDFLARE_TUNNEL_TOKEN` | `CLOUDFLARE_TUNNEL_TOKEN` — токен Cloudflare Tunnel.                                          |
| `AUTH_B2C_CLIENTID`       | `AUTH_B2C_CLIENTID` — client ID Azure AD B2C для DataManager.                                 |
| `AUTH_B2C_CLIENT_SECRET`  | `AUTH_B2C_CLIENT_SECRET` — client secret Azure AD B2C для DataManager.                        |

Итого **9 GitHub Secrets**.

### Захардкоженные значения в `deploy.yml`

Эти значения подставляются в `.env` напрямую из workflow, вне Secrets:

| Переменная        | Значение                |
|-------------------|-------------------------|
| `POSTGRES_HOST`   | `bdc_pgbouncer`         |
| `POSTGRES_DB`     | `market_analytics`      |
| `SERILOG_SEQ_URL` | `http://seq:5341`       |
| `SEQ_ADMIN_USER`  | `lex` ⚠️                 |
| `SEQ_ADMIN_PASS`  | `lex` ⚠️                 |
| `RABBITMQ_PORT`   | `5672`                  |
| `RABBITMQ_HOST`   | `bdc_rabbitmq`          |

⚠️ `SEQ_ADMIN_PASS=lex` — пароль администратора Seq в открытом виде в YAML.
Зафиксировано в `TECH_DEBT.md` для перевода в Secrets.

⚠️ `SERILOG_SEQ_URL=http://seq:5341` указывает на хост `seq`, но в проде
контейнер называется `bdc_seq`, и в самих сервисах перебивается через
`Serilog__WriteTo__0__Args__serverUrl=http://bdc_seq:80` (см. compose).
Скорее всего значение из `.env` фактически не используется. Также
зафиксировано в `TECH_DEBT.md`.

---

## 5. GHCR (GitHub Container Registry)

- **Worker:** `ghcr.io/alexpaturov/binancedatacollector/worker:<7-char-sha>`
- **DataManager:** `ghcr.io/alexpaturov/binancedatacollector/datamanager:<7-char-sha>`

Тэг `:latest` **не ставится** — каждый push в master создаёт уникальный
иммутабельный тэг. Это даёт детерминированный откат, но требует
ручной правки `APP_VERSION` (см. раздел 11).

Compose-файл (`docker-compose.prod.yml`) подставляет переменную
`${APP_VERSION}` в строку `image:` для обоих сервисов:

```yaml
bdc_worker:
  image: ghcr.io/alexpaturov/binancedatacollector/worker:${APP_VERSION}
bdc_datamanager:
  image: ghcr.io/alexpaturov/binancedatacollector/datamanager:${APP_VERSION}
```

---

## 6. Self-hosted runner

- Установлен на `analserver` — том же сервере, куда и происходит деплой.
  Это сделано осознанно: серый IP, нет необходимости в SSH-action из
  внешнего runner'а.
- Регистрация — через `./config.sh` после получения токена в
  `Settings → Actions → Runners`. Подробности — `docs/prod/05_setup.md`,
  раздел 5.
- Запускается как systemd-сервис: `sudo ./svc.sh install` + `sudo ./svc.sh start`.
- Workflow указывает `runs-on: self-hosted` **без labels**. ⚠️ Если в
  будущем зарегистрировать второй runner, поведение станет
  недетерминированным — job уйдёт на любой свободный. Зафиксировано в
  `TECH_DEBT.md`.

---

## 7. Что разворачивается на сервере

`docker-compose.prod.yml` объявляет следующий стэк:

| Контейнер         | Образ                                             | Назначение                                                            |
|-------------------|---------------------------------------------------|-----------------------------------------------------------------------|
| `traefik`         | `traefik:v2.10`                                   | Reverse proxy, TLS-терминация, Let's Encrypt (HTTP-01 challenge).     |
| `cloudflared`     | `cloudflare/cloudflared:latest`                   | Cloudflare Tunnel — единственная точка входа извне.                   |
| `bdc_db`          | `postgres:16-alpine`                              | PostgreSQL 16. Порт `5432` биндится на Tailscale-IP `100.96.120.16`. |
| `bdc_pgbouncer`   | `edoburu/pgbouncer`                               | Connection pooler перед Postgres, слушает `6432`. `POOL_MODE=transaction`. |
| `bdc_rabbitmq`    | `rabbitmq:3-management-alpine`                    | Брокер сообщений.                                                     |
| `bdc_seq`         | `datalust/seq:latest`                             | Централизованные логи (Serilog).                                      |
| `bdc_worker`      | `ghcr.io/.../worker:${APP_VERSION}`               | Сборщик + Hangfire-серверы.                                           |
| `bdc_datamanager` | `ghcr.io/.../datamanager:${APP_VERSION}`          | Backoffice / UI с Azure AD B2C.                                       |
| `uptime_kuma`     | `louislam/uptime-kuma:1`                          | Мониторинг доступности.                                               |

### Внешние домены через Traefik (`*.jahasim.com`)

| Хост                        | Куда роутит                              |
|-----------------------------|------------------------------------------|
| `worker.jahasim.com`        | `bdc_worker` (HTTP API).                 |
| `hangfire.jahasim.com`      | `bdc_worker` (Hangfire dashboard).       |
| `datamanager.jahasim.com`   | `bdc_datamanager` (UI).                  |
| `seq.jahasim.com`           | `bdc_seq` (UI логов).                    |
| `status.jahasim.com`        | `uptime_kuma` (UI мониторов).            |

Все 5 доменов проходят через Cloudflare Tunnel → Traefik → нужный сервис.
Прямого внешнего IP у сервера нет.

### Сети (external, создаются workflow'ом)

- `binancecollector_web` — для всего, что выставлено наружу через Traefik.
- `internal_network` — для межсервисного трафика (БД, очереди, логи).

---

## 8. Хранилища

### 8.1. Данные PostgreSQL — внешний диск (bind mount)

Данные БД **не в Docker volume**, а на внешнем 4TB-диске, примонтированном в `/mnt/ext`:

```yaml
bdc_db:
  volumes:
    - /mnt/ext/postgres_data:/var/lib/postgresql/data
```

> Если `bdc_db` стартует с непримонтированным диском, Postgres молча создаст пустую БД на системном диске. Подробности и защита — `docker/docs/README_VOLUMES.md`.

### 8.2. Volumes (external, ручное создание)

Объявлены в `docker-compose.prod.yml` как `external: true`, создаются workflow'ом через `docker volume create … || true` при каждом деплое (идемпотентно). **Удаление любого = потеря данных.**

| Volume                                  | Что хранит                                        |
|-----------------------------------------|---------------------------------------------------|
| `binancecollector_seq_data`             | Логи Seq (`/data`).                               |
| `binancecollector_rabbitmq_data`        | Состояние очередей RabbitMQ.                      |
| `binancecollector_letsencrypt_data`     | TLS-сертификаты Let's Encrypt (`acme.json`).      |
| `binancecollector_bdc_data`             | Рабочие данные Worker'а (CSV-архивы и т.п.). Монтируется в `/opt/bdc_data` и в Worker'е, и в DataManager'е. |
| `binancecollector_uptime_kuma_data`     | Состояние Uptime Kuma (мониторы, история).        |

Подробнее по сети и томам — `docs/Server_Network_Config.md`, `docker/docs/README_VOLUMES.md`.

---

## 9. Процесс развёртывания

С точки зрения разработчика:

1. Сделать изменения в feature-ветке.
2. Открыть PR в `master`. ⚠️ Это **уже** триггерит workflow и деплой на
   прод (см. раздел 1 и `TECH_DEBT.md`).
3. После ревью — merge в `master`. Workflow запустится повторно.
4. Через 5–10 минут (сборка двух образов + push + pull) новая версия на
   проде.

Никаких ручных команд `docker build` / `scp` / `docker compose` на
сервере при штатной выкатке не требуется.

---

## 10. Проверка после деплоя

Подключение по SSH идёт через Tailscale:

```bash
ssh prod
cd /opt/BinanceCollector/docker/compose
```

### Состояние контейнеров

```bash
docker compose ps
```

### Логи

```bash
# Логи Worker'а в реальном времени
docker compose logs -f bdc_worker

# Логи DataManager'а
docker compose logs -f bdc_datamanager

# Все сервисы разом (с цветами по сервисам)
docker compose logs -f
```

⚠️ Старая версия документа предлагала `docker compose logs -f app` —
**сервиса `app` не существует**, эта команда была устаревшей с момента
разделения на `bdc_worker` и `bdc_datamanager`.

### Веб-интерфейсы

| Что проверяем                | Где                                  | Что должно быть                                        |
|------------------------------|--------------------------------------|--------------------------------------------------------|
| Доступность всех сервисов    | https://status.jahasim.com           | Все мониторы в Uptime Kuma зелёные.                    |
| Hangfire-джобы               | https://hangfire.jahasim.com         | Recurring jobs выполняются, нет накопившихся Failed.    |
| Логи приложений              | https://seq.jahasim.com              | Свежие сообщения `SERVICE STARTED` / `SERVICE READY`.  |
| DataManager UI               | https://datamanager.jahasim.com      | Страница открывается, B2C-логин работает.              |

---

## 11. Откат версии

Поскольку тэг `:latest` не используется, откат — ручной.

1. Найти нужный 7-символьный SHA в GHCR (страница пакета в GitHub) или
   в истории коммитов master.
2. На сервере:

```bash
ssh prod
cd /opt/BinanceCollector/docker/compose
sudo nano .env                 # поправить APP_VERSION на нужный SHA
docker compose pull
docker compose up -d
```

Поскольку `.env` пересоздаётся пайплайном на каждом следующем деплое,
ручная правка живёт только до следующего push'а в `master`.

---

## 12. Известные проблемы

Подробности и план разбора — в `docs/TECH_DEBT.md`, раздел 1. Краткий
список, относящийся к этому документу:

- **`pull_request` триггер запускает деплой на прод** — любой PR в
  `master` уезжает в продакшен.
- **`SEQ_ADMIN_PASS=lex` захардкожен в `deploy.yml`** — должен быть в Secrets.
- **Утечка секретов в логи Actions** — `sed`-маскировка покрывает
  только `PASSWORD=`, `CLOUDFLARE_TUNNEL_TOKEN` и `AUTH_B2C_CLIENT_SECRET`
  идут в лог открытым текстом.
- **`docker compose pull/up -d` без `-f` флагов** — поведение зависит от
  того, что фактически лежит в `/opt/BinanceCollector/docker/compose/`.
- **Нет тэга `:latest`** — откат ручной (раздел 11). Возможно, сделано
  намеренно ради детерминированности.
- **Нет `dotnet test` в pipeline** — тесты в CI не запускаются.
- **`runs-on: self-hosted` без labels** — при появлении второго runner'а
  поведение станет недетерминированным.
- **`SERILOG_SEQ_URL=http://seq:5341` в `.env`** не совпадает с реальным
  именем контейнера (`bdc_seq`) и портом (`80`); вероятно
  перебивается переменными окружения сервиса и фактически не
  используется.
- **Шаг `List files for debugging: ls -R`** засоряет лог Actions, остался
  от первоначальной отладки.
