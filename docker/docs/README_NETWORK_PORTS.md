# 🌐 README_NETWORKS_AND_PORTS

Цель документа — **зафиксировать сетевую модель и порты BinanceDataCollector**.

Без воды. Без объяснений "как Docker работает".
Только то, что **есть** и **почему трогать нельзя**.

---

## 🧠 Базовый принцип

> **Ни один сервис не должен быть доступен напрямую, если это не указано явно.**

Если порт или сеть существуют — значит:

- либо это контракт
- либо это осознанное исключение

---

## 🌐 Сети Docker

### 1. `internal_network`

**Тип:** bridge (internal)

**Назначение:**

- внутренняя связь сервисов
- недоступна извне

**Подключены:**

- Postgres (`bdc_db`)
- PgBouncer (`bdc_pgbouncer`)
- RabbitMQ (`bdc_rabbitmq`)
- Worker (`bdc_worker`)
- DataManager (`bdc_datamanager`)
- Seq (`bdc_seq`)

❗ **Запреты:**

- нельзя делать external
- нельзя прокидывать наружу
- нельзя подключать Traefik напрямую

---

### 2. `binancecollector_web`

**Тип:** external

**Назначение:**

- входная точка HTTP(S)
- взаимодействие с Traefik

**Подключены:**

- Traefik
- Cloudflare Tunnel
- Seq (через Traefik)
- Uptime Kuma
- Worker / DataManager (ТОЛЬКО через Traefik)

❗ Эта сеть **создаётся заранее** и не должна пересоздаваться Docker Compose.

---

## 🔌 Порты (фактические)

### 🧱 Инфраструктура

| Сервис  | Порт | Где доступен | Назначение             |
| ------- | ---- | ------------ | ---------------------- |
| Traefik | 80   | Internet     | HTTP entrypoint        |
| Traefik | 443  | Internet     | HTTPS entrypoint       |
| Traefik | 8080 | Server       | Dashboard (restricted) |

---

### 🗄 Хранилища

| Сервис    | Порт | Где                       | Комментарий                                                |
| --------- | ---- | ------------------------- | ---------------------------------------------------------- |
| Postgres  | 5432 | Server (specific IP bind) | Проброс ТОЛЬКО для DBeaver, ограничен IP (e.g. 100.96.x.x) |
| PgBouncer | 6432 | Server                    | Контролируемый доступ                                      |

❗ Эти порты **не используются Traefik**.
❗ Проброс Postgres на сервер **осознанное исключение**, а не правило.

---

### 🐇 RabbitMQ

| Порт  | Назначение    | Доступ             |
| ----- | ------------- | ------------------ |
| 5672  | AMQP          | internal_network   |
| 15672 | Management UI | **НЕ ПУБЛИКУЕТСЯ** |

❗ UI RabbitMQ **намеренно закрыт**.

---

### 📊 Seq

| Порт | Где            | Назначение       |
| ---- | -------------- | ---------------- |
| 80   | internal / web | UI через Traefik |
| 5341 | internal       | Ingest (Serilog) |

❗ Прямой доступ к 5341 извне запрещён.

---

### 🧪 Health endpoints

| Сервис      | Endpoint        | Где доступен  |
| ----------- | --------------- | ------------- |
| Worker      | `/health/live`  | через Traefik |
| Worker      | `/health/ready` | через Traefik |
| DataManager | `/health/live`  | через Traefik |
| DataManager | `/health/ready` | через Traefik |

Используются:

- Traefik
- Uptime Kuma

---

### ⏱ Uptime Kuma

| Порт | Где            | Назначение       |
| ---- | -------------- | ---------------- |
| 3001 | internal / web | UI через Traefik |

❗ Kuma **не лезет** в internal_network напрямую — только HTTP checks.

---

## 🚫 Запрещённые действия

- добавлять новые published ports без документации
- публиковать сервисы минуя Traefik
- менять сеть сервиса без понимания последствий
- использовать `network_mode: host`

---

## 🛑 Если что-то не работает

Порядок проверки:

1. DNS / Cloudflare
2. Traefik dashboard
3. Health endpoints
4. Docker networks
5. Только потом — код

---

## ✅ Итог

Сетевая модель:

- минимальна
- контролируема
- безопасна

Если хочется что-то «упростить» —
**сначала обнови этот файл, потом делай.**
