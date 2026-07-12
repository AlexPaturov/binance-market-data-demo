# Документация: ARCHITECTURE_PROD — PROD-окружение

Этот документ описывает физическую и сетевую модель **прод-окружения** проекта `BinanceDataCollector`.

> Для практической эксплуатации (как деплоить, что считается аварией и т.д.) см. `docker/docs/README_PRODUCTION.md`. Здесь — про **железо, сеть и состав сервисов**.

---

## 1. Физическая инфраструктура

- **Сервер:** GMKtec G2 (mini-PC), CPU **Intel N150**.
- **Hostname:** `analserver`.
- **ОС:** Ubuntu 24.04 LTS.
- **Размещение:** сервер стоит дома, подключён к локальной сети роутера, **прямого внешнего IP не имеет**.
- **Доступ извне:** только через **Cloudflare Tunnel** (без проброса портов на роутере).
- **Доступ для админа:** SSH из локалки и через Tailscale.

Всё прикладное ПО работает **в Docker**. Никаких .NET-приложений на хост-системе, в отличие от dev'а.

---

## 2. Состав сервисов

В prod-стэке параллельно работают:

- **Traefik** — reverse proxy, TLS-терминация для внутренних сервисов.
- **Cloudflare Tunnel** — единственный канал для внешнего трафика.
- **Postgres 16** (`bdc_db`) — основная СУБД.
- **PgBouncer** (`bdc_pgbouncer`) — пул подключений перед Postgres.
- **RabbitMQ** (`bdc_rabbitmq`) — брокер сообщений.
- **Seq** (`bdc_seq`) — централизованные логи.
- **Worker** (`bdc_worker`) — сбор и агрегация данных с Binance.
- **DataManager** (`bdc_datamanager`) — backoffice / управление.
- **Uptime Kuma** — мониторинг доступности.

Образы Worker и DataManager берутся из GHCR (`ghcr.io/alexpaturov/binancedatacollector/*`). CI/CD — GitHub Actions с self-hosted runner на самом сервере; push в `master` ⇒ автоматический redeploy.

---

## 3. Сетевая модель

```
   Internet
      │
      ▼
 Cloudflare (TLS)
      │
      ▼
 Cloudflare Tunnel ───► Traefik ──► Worker / DataManager / Seq / Kuma
                                        │
                                        ▼
                          Postgres / PgBouncer / RabbitMQ
                          (только во внутренней Docker-сети)
```

- Внешний трафик попадает на сервер **только** через Cloudflare Tunnel — никаких проброшенных портов на роутере нет.
- TLS терминируется дважды: Cloudflare (наружу) и Traefik (между туннелем и сервисами).
- Сервисы домена **`jahasim.com`** маршрутизируются Traefik'ом.
- Postgres из внешнего мира недоступен. Для DBeaver используется **Tailscale IP `100.96.120.16:5432`** — это единственная легальная точка прямого доступа к БД.
- Две Docker-сети: `binancecollector_web` (external) и `internal_network` (external). Обе создаются вручную один раз и **не пересоздаются** Compose'ом.

---

## 4. Базы данных

Внутри Postgres-контейнера работают **две независимых БД**:

- **`market_analytics`** — основная бизнес-БД (трейды, OHLCV, tracked symbols).
- **`market_analytics_jobs`** — служебная БД для Hangfire (очереди, расписания, состояния джобов).

Разделение позволяет независимо бэкапить, чинить и масштабировать рабочие данные и Hangfire.

---

## 5. Хранилища

### 5.1. Данные PostgreSQL — внешний диск

Данные БД лежат на **внешнем 4TB-диске**, примонтированном в `/mnt/ext` (`/etc/fstab`, UUID `25ddc534-13e6-479e-8392-a4487a975c80`, опция `nofail`). В `docker-compose.prod.yml` это **bind mount** у сервиса `bdc_db`:

```yaml
bdc_db:
  volumes:
    - /mnt/ext/postgres_data:/var/lib/postgresql/data
```

> Перед стартом `bdc_db` диск обязан быть примонтирован (`mountpoint /mnt/ext`). Иначе Postgres проинициализирует пустую базу в директории на системном диске. См. `docker/docs/README_DO_NOT_TOUCH.md`.

### 5.2. Остальные volume'ы

**External named**, создаются вручную и **никогда не удаляются** автоматически:

- `binancecollector_seq_data`
- `binancecollector_rabbitmq_data`
- `binancecollector_letsencrypt_data` — сертификаты Let's Encrypt
- `binancecollector_bdc_data` — архивы и CSV

> Удаление любого из них = потеря данных или rate-limit от Let's Encrypt. См. `docker/docs/README_DO_NOT_TOUCH.md`.

---

## 6. Compose-файлы

PROD запускается **строго** через два файла:

- `docker/compose/docker-compose.yml` — базовый.
- `docker/compose/docker-compose.prod.yml` — прод-надстройка.

Запрещено:

- запускать прод без базового compose;
- использовать `docker-compose.dev.yml` на сервере.

---

## 7. Чек-лист "прод жив"

- Traefik маршрутизирует домены `jahasim.com`.
- Uptime Kuma — все мониторы зелёные.
- `/health/live` и `/health/ready` Worker'а и DataManager'а отвечают `200`.
- В Seq есть свежие сообщения `SERVICE STARTED / READY`.

Если хотя бы один пункт не выполняется — деплой не считается успешным, см. `README_PRODUCTION.md`.
