# 🚀 README_PRODUCTION

Этот документ описывает **как именно устроен и запускается PROD BinanceDataCollector**.

Цель:

- быстро вспомнить логику прод-окружения
- безопасно деплоить изменения
- не сломать систему "одним docker compose up"

Этот файл **дополняет**, а не заменяет `README_DOCKER_RUN.md`.

---

## ❗ Что считается PROD

PROD — это окружение, в котором:

- сервисы доступны из интернета
- используются реальные домены
- данные считаются ценными
- допустим только контролируемый даунтайм

Если есть сомнения — **считай, что это PROD**.

---

## 🧱 Состав PROD окружения

В проде **одновременно работают**:

- Traefik (reverse proxy)
- Cloudflare Tunnel
- Postgres
- PgBouncer
- RabbitMQ
- Seq
- Worker
- DataManager
- Uptime Kuma

❗ Все сервисы запускаются **через Docker Compose**.

---

## 🐳 Compose-файлы PROD

PROD запускается **строго** так:

- `compose/docker-compose.yml` — базовый
- `compose/docker-compose.prod.yml` — прод-надстройка

🚫 НЕЛЬЗЯ:

- запускать прод без базового compose
- использовать `docker-compose.dev.yml` на сервере

---

## 💾 Внешний диск — читать до запуска

Данные PostgreSQL живут на внешнем 4TB-диске, примонтированном в `/mnt/ext` (bind mount, не Docker volume — см. `README_VOLUMES.md`).

❗ Если поднять `bdc_db` при непримонтированном диске, Postgres **молча создаст новую пустую базу** на системном диске. Приложение поднимется и подключится, но таблиц не будет.

Защита на двух уровнях:

- **Автозапуск (ребут):** drop-in `/etc/systemd/system/docker.service.d/wait-for-ext.conf` с `RequiresMountsFor=/mnt/ext` — Docker не стартует, пока диск не примонтирован.
- **Ручной запуск:** `prod-start.sh` (см. ниже) проверяет монтирование, наличие `PG_VERSION` и партиций.

---

## 🚀 Запуск и остановка (скрипты)

Скрипты лежат в репозитории (`docker/prod-start.sh`, `docker/prod-stop.sh`) и **не разносятся деплоем** — на сервер копируются вручную в `/opt/BinanceCollector/docker/`.

**Запуск:**
```bash
/opt/BinanceCollector/docker/prod-start.sh
```
Монтирует диск при необходимости, отказывается стартовать без реального каталога данных, ждёт `bdc_db` до healthy, проверяет что партиции `Trades` на месте (а не пустая БД) и что Worker может писать в `bdc_data`.

**Остановка:**
```bash
/opt/BinanceCollector/docker/prod-stop.sh              # остановить + отмонтировать диск
/opt/BinanceCollector/docker/prod-stop.sh --poweroff   # то же + выключить сервер
```
Сначала гасит приложения, затем остальное с таймаутом 120с (дефолтных 10 Postgres'у мало), **проверяет `database system is shut down` в логах** и только после этого отмонтирует диск и паркует головки.

> Незавершённые джобы импорта прерывать безопасно: ZIP/CSV удаляются только после успешной вставки, вставка идемпотентна. Очередь доработает при следующем старте.

---

## 🔄 Общий порядок запуска

Реальный порядок инициализации:

1. Монтирование `/mnt/ext` (данные Postgres)
2. Docker daemon
3. External volumes (должны существовать заранее)
4. External network `binancecollector_web`
5. Traefik
6. Cloudflare Tunnel
7. Postgres
8. PgBouncer
9. RabbitMQ
10. Seq
11. Worker / DataManager
12. Uptime Kuma

❗ Docker Compose сам оркестрирует запуск, но **понимание порядка критично при дебаге**.

---

## 🌐 Внешний доступ

В проде **нет прямого доступа** к сервисам.

Весь внешний трафик:

```
Internet
  ↓
Cloudflare
  ↓
Cloudflare Tunnel
  ↓
Traefik
  ↓
Worker / DataManager / Seq / Kuma
```

🚫 Прямой проброс портов = нарушение модели безопасности.

---

## 🔐 TLS и сертификаты

- TLS терминируется:

  - Cloudflare
  - Traefik

- Сертификаты хранятся в volume:

  - `binancecollector_letsencrypt_data`

🚫 Удаление volume = потеря сертификатов и rate-limit от Let's Encrypt.

---

## 🧪 Обновление PROD (деплой)

Рекомендуемый порядок:

1. Убедиться, что CI успешно собрал образы
2. Проверить `.env`
3. Выполнить:

   ```bash
   docker compose -f compose/docker-compose.yml -f compose/docker-compose.prod.yml pull
   docker compose -f compose/docker-compose.yml -f compose/docker-compose.prod.yml up -d
   ```

4. Проверить:

   - Traefik dashboard
   - Uptime Kuma
   - логи в Seq

---

## 🔍 Как понять, что PROD жив

Признаки нормального состояния:

- Traefik маршрутизирует домены
- Uptime Kuma — зелёная
- `/health/live` и `/health/ready` отвечают
- в Seq есть `SERVICE STARTED / READY`

Если этого нет — **не считать деплой успешным**.

---

## 🚫 Что считается аварией

- потеря external volume
- падение Traefik
- недоступность Cloudflare Tunnel
- отсутствие логов в Seq

При аварии:

- не делай `docker volume prune`
- не пересоздавай всё подряд

Сначала — **логи и состояние контейнеров**.

---

## 🛑 Итоговое правило

> В проде нельзя экспериментировать.

Если нужно проверить гипотезу:

- делай это локально
- или в DEV окружении

PROD — это зона исполнения, а не экспериментов.
