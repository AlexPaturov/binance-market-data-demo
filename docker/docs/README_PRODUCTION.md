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

## 🔄 Общий порядок запуска

Реальный порядок инициализации:

1. Docker daemon
2. External volumes (должны существовать заранее)
3. External network `binancecollector_web`
4. Traefik
5. Cloudflare Tunnel
6. Postgres
7. PgBouncer
8. RabbitMQ
9. Seq
10. Worker / DataManager
11. Uptime Kuma

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
