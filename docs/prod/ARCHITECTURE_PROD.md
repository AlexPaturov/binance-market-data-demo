# PROD — устройство и эксплуатация

Железо, сеть, хранилища и порядок запуска прод-окружения.

> Смежное: [`04_deployment.md`](./04_deployment.md) — CI/CD · [`network.md`](./network.md) — порты и firewall · [`../../docker/docs/README_VOLUMES.md`](../../docker/docs/README_VOLUMES.md) — что нельзя удалять.

---

## 1. Железо

- **Сервер:** GMKtec G2 (mini-PC), CPU Intel N150, hostname `analserver`.
- **ОС:** Ubuntu 24.04 LTS.
- **Размещение:** дома, за роутером, внешнего IP нет.
- **Доступ извне:** только Cloudflare Tunnel — портов на роутере не проброшено.
- **Доступ админа:** SSH из локалки и через Tailscale (`100.96.120.16:2237`).

Всё прикладное ПО работает в Docker.

---

## 2. Состав сервисов

| Контейнер | Роль |
| :--- | :--- |
| Traefik | reverse proxy, TLS между туннелем и сервисами |
| Cloudflare Tunnel | единственный вход снаружи |
| `bdc_db` | PostgreSQL 16 |
| `bdc_pgbouncer` | пул подключений |
| `bdc_rabbitmq` | брокер сообщений |
| `bdc_seq` | централизованные логи |
| `bdc_worker` | сбор, агрегация, индикаторы, Hangfire |
| `bdc_datamanager` | backoffice, графики, панель качества данных |
| Uptime Kuma | мониторинг доступности |

Образы Worker и DataManager — из GHCR, тег = SHA коммита.

**Метрики конвейера** — отдельный compose-проект `bdc_monitoring` (Prometheus, node_exporter,
два postgres_exporter, Grafana), поднимается независимо от app-стека, доступен через
Tailscale. Подробности — [`observability.md`](./observability.md).

---

## 3. Сеть

```
Internet → Cloudflare (TLS) → Cloudflare Tunnel → Traefik → Worker / DataManager / Seq / Kuma
                                                               │
                                                               ▼
                                              Postgres / PgBouncer / RabbitMQ
                                              (только внутренняя Docker-сеть)
```

- Домены `jahasim.com` маршрутизирует Traefik.
- Postgres снаружи недоступен. Для DBeaver — Tailscale `100.96.120.16:5432`.
- Две external-сети: `binancecollector_web` и `internal_network`. Создаются один раз вручную, Compose их не пересоздаёт.

---

## 4. Базы данных

- **`market_analytics`** — рабочая: тики, свечи, индикаторы, фичи стакана, качество данных.
- **`market_analytics_jobs`** — Hangfire: очереди, расписания, состояния джобов.

Разделение даёт независимый бэкап и починку: развалившийся Hangfire не тянет за собой рыночные данные.

---

## 5. Хранилища

### 5.1. Данные PostgreSQL — горячий SSD + холодный внешний диск

Раскладка по силе дисков ([ADR 0011](../adr/0011-hot-cold-tiering-pg-cron.md)): случайный доступ конвейера живёт на быстром SSD, холодная история — на ёмком HDD.

```yaml
bdc_db:                       # образ собирается из docker/postgres/Dockerfile:
  build: { context: ../postgres }   # postgres:16 (Debian) + postgresql-16-cron
  volumes:
    - /opt/BinanceCollector/pgdata:/var/lib/postgresql/data   # PGDATA — внутренний SSD
    - /mnt/ext/pg_tablespaces:/mnt/pg_tablespaces             # холодное пространство — внешний HDD
```

- **PGDATA (каталоги, WAL, активные партиции)** — на внутреннем SATA SSD в `/opt/BinanceCollector/pgdata`. IOPS для случайного доступа.
- **Табличное пространство `cold`** — на внешнем 4 TB диске в `/mnt/ext/pg_tablespaces/cold` (fstab, UUID `25ddc534-13e6-479e-8392-a4487a975c80`, опция `nofail`; каталог принадлежит uid postgres). Закрытые месяцы `Trades` переезжают сюда `pg_cron`-джобой раз в час.
- **Коллация базы — `C.UTF-8`** (задаётся на `initdb`, `POSTGRES_INITDB_ARGS`): байтовый порядок, независимый от libc.

Отключён внешний диск — свежие данные работают, запросы за эвакуированные месяцы падают с `undefined_file`.

**Если поднять `bdc_db` при непримонтированном внешнем диске, `CREATE TABLESPACE cold` на чистой инициализации не пройдёт, а на живой базе запросы к холодным партициям упрутся в отсутствующие файлы.** Защита: drop-in `/etc/systemd/system/docker.service.d/wait-for-ext.conf` с `RequiresMountsFor=/mnt/ext` — Docker не стартует, пока диск не примонтирован; `prod-start.sh` проверяет монтирование.

### 5.2. External named volumes

Создаются вручную, автоматически не удаляются:

- `binancecollector_seq_data`
- `binancecollector_rabbitmq_data`
- `binancecollector_letsencrypt_data` — сертификаты Let's Encrypt
- `binancecollector_bdc_data` — архивы и CSV

Удаление любого = потеря данных или rate-limit от Let's Encrypt. См. [`README_VOLUMES.md`](../../docker/docs/README_VOLUMES.md).

---

## 6. Запуск и остановка

Скрипты лежат в репозитории (`docker/prod-start.sh`, `docker/prod-stop.sh`) и **деплоем не разносятся** — копируются на сервер вручную в `/opt/BinanceCollector/docker/`.

```bash
/opt/BinanceCollector/docker/prod-start.sh             # монтирует диск, поднимает стек, проверяет БД
/opt/BinanceCollector/docker/prod-stop.sh              # гасит стек и отмонтирует диск
/opt/BinanceCollector/docker/prod-stop.sh --poweroff   # то же + выключение сервера
```

`prod-start.sh` отказывается стартовать, если диск не примонтирован или в каталоге данных нет `PG_VERSION`; после подъёма ждёт `bdc_db` до healthy и проверяет, что партиции `Trades` на месте (а не пустая БД).

`prod-stop.sh` останавливает приложения первыми, остальное — с таймаутом 120 с (дефолтных 10 Postgres'у не хватает), **дожидается `database system is shut down` в логах** и только потом отмонтирует диск и паркует головки.

> Прерывать импорт архивов безопасно: ZIP/CSV удаляются только после успешной вставки, вставка идемпотентна. Очередь доработает при следующем старте.

Если по какой-то причине скриптов нет, прод поднимается строго двумя compose-файлами:

```bash
docker compose -f compose/docker-compose.yml -f compose/docker-compose.prod.yml up -d
```

`docker-compose.dev.yml` на сервере не используется.

---

## 7. Чек-лист «прод жив»

- Traefik маршрутизирует `jahasim.com`.
- Uptime Kuma — мониторы зелёные.
- `/health/live` и `/health/ready` Worker'а и DataManager'а отвечают `200`.
- В Seq есть свежие `SERVICE STARTED / READY`.
- В БД растут `Trades` — свежий тик отстаёт от `now()` на секунды:

```bash
docker exec bdc_db psql -U bindatacoll -d market_analytics -c \
  'SELECT max("TradeTime") FROM public."Trades";'
```

Пока хотя бы один пункт не выполнен, деплой не считается успешным.

---

## 8. При аварии

Сначала логи и состояние контейнеров:

```bash
docker ps --format '{{.Names}}\t{{.Image}}\t{{.Status}}'
docker logs bdc_worker --tail=100
```

`docker volume prune` и пересоздание стека «с нуля» — не инструменты диагностики: они уничтожают данные, а не чинят их.
