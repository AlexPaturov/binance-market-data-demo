# 04 — Развёртывание и CI/CD

Источник истины — `.github/workflows/deploy.yml`, `docker/Dockerfile` и compose-файлы. Этот документ описывает, что там происходит.

> Смежное: [`05_setup.md`](./05_setup.md) — настройка сервера и runner'а · [`ARCHITECTURE_PROD.md`](./ARCHITECTURE_PROD.md) — железо и состав сервисов · [`../TECH_DEBT.md`](../TECH_DEBT.md) — известные проблемы.

---

## Два job'а

### `build-and-test` — на `ubuntu-latest`

Запускается на **каждый** push в `master` и на **каждый** Pull Request.

```
restore → build (Release) → dotnet test → publish обоих приложений
```

Шаг `publish` нужен, чтобы поймать ошибки, которые всплывают только при публикации, — до того, как дело дойдёт до деплоя.

### `deploy` — на self-hosted runner

```yaml
needs: build-and-test
if: github.event_name != 'pull_request' && github.ref == 'refs/heads/master'
```

То есть **Pull Request прод не трогает** — только сборка и тесты. Деплой идёт лишь при push в `master` (или вручную через `workflow_dispatch`).

Runner стоит **на самом прод-сервере**: у сервера серый IP, и это заодно избавляет от push образов через интернет.

---

## Что делает деплой

1. Собирает два образа приложения из одного multi-stage `docker/Dockerfile`:
   - `--target worker` → `ghcr.io/alexpaturov/binancedatacollector/worker`
   - `--target datamanager` → `ghcr.io/alexpaturov/binancedatacollector/datamanager`
2. Пушит их в GHCR. Аутентификация — встроенным `GITHUB_TOKEN`, не PAT: он не протухает.
3. Копирует `docker-compose.prod.yml` в `/opt/BinanceCollector/docker/compose/`.
4. Генерирует там `.env` из GitHub Secrets.
5. Идемпотентно создаёт external volumes и networks (`... || true`).
6. Собирает образ БД локально: `docker compose build bdc_db` (`docker/postgres/Dockerfile` = postgres:16 + pg_cron; его нет в реестре, только сборка). Кэш слоёв на self-hosted раннере делает пересборку мгновенной, image id не меняется — живая база не пересоздаётся.
7. `docker compose pull --ignore-buildable && docker compose up -d`. `--ignore-buildable` пропускает `bdc_db` (иначе `pull` падает на «repository does not exist» для локального образа).

**Версионирование:** `APP_VERSION = ${GITHUB_SHA::7}`. Тега `:latest` нет — каждый push даёт уникальный иммутабельный тег, и всегда видно, что именно крутится в проде.

---

## Секреты

Читаются из GitHub Secrets и попадают в `.env` на сервере:

| Secret | Куда |
| :--- | :--- |
| `DB_USER`, `DB_PASSWORD` | `POSTGRES_USER`, `POSTGRES_PASSWORD` |
| `RABBITMQ_USER`, `RABBITMQ_PASS` | `RABBITMQ_USER`, `RABBITMQ_PASSWORD` (имена намеренно разные — не путать) |
| `ACME_EMAIL` | Let's Encrypt в Traefik |
| `CLOUDFLARE_TUNNEL_TOKEN` | Cloudflare Tunnel |
| `AUTH_B2C_CLIENTID`, `AUTH_B2C_CLIENT_SECRET` | Azure AD B2C для DataManager |
| `GRAFANA_ADMIN_PASSWORD` | пароль admin в Grafana (мониторинг-стек) |
| `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` | доставка алертов Grafana в Telegram |

Логин в GHCR идёт встроенным `GITHUB_TOKEN` — отдельного секрета не нужно. Репозиторию выдано право `write` на пакеты `worker` и `datamanager`.

---

## Что образы НЕ везут

**Скрипты `prod-start.sh` / `prod-stop.sh`** деплой не разносит — их кладут на сервер вручную:

```bash
scp -P 2237 docker/prod-*.sh lex@100.96.120.16:/opt/BinanceCollector/docker/
```

**Миграции БД** не привязаны к деплою кода. Baseline `02_baseline.sql` поднимает чистый том; в живую базу изменения накатывает раннер `docker/postgres/migrate.sh` по журналу `schema_migrations` — идемпотентно, только непринятое. Триггер — отдельный workflow **`migrate.yml`**, оператор запускает по кнопке, независимо от деплоя приложения:

```
Actions → Apply DB migrations to production → Run workflow → ввод APPLY
```

Так осознанно: миграция, снесённая автодеплоем не вовремя (rewrite таблицы, `SET TABLESPACE` минутами), на одноузловом проде дороже ручного шага. Тайминг тяжёлых выбирает человек; `lock_timeout`/`statement_timeout` в раннере превращают «повесил конвейер» в аборт. Что уже накатано — в `schema_migrations`; что не свёрнуто в baseline — ловит CI-страж (build-and-test). Модель целиком — [ADR 0013](../adr/0013-schema-baseline-and-migration-automation.md).

---

## Stages в Dockerfile

| Stage | База | Что делает |
| :--- | :--- | :--- |
| `base-build` | `dotnet/sdk:8.0` | `restore` + копирование исходников |
| `build-worker` / `build-datamanager` | `base-build` | `dotnet publish` |
| **`worker`** | `dotnet/aspnet:8.0-bookworm-slim` | Прод-образ Worker'а. Работает под `USER app` (uid 1654) |
| **`datamanager`** | то же | Прод-образ DataManager'а |
| `worker-dev` / `datamanager-dev` | `dotnet/sdk:8.0` | `dotnet watch run`, только для `docker-compose.dev.yml` |

Прод-пайплайн dev-stages не собирает — цели указаны явно.

---

## Проверка после деплоя

```bash
docker ps --format '{{.Names}}\t{{.Image}}\t{{.Status}}'
```

Образы Worker'а и DataManager'а должны быть с тегом свежего коммита. Дальше — [чек-лист «прод жив»](./ARCHITECTURE_PROD.md#7-чек-лист-прод-жив).
