# Сеть и безопасность прод-сервера

**Последнее обновление:** май 2026  
**Сервер:** `analserver` — GMKtec G2 (Intel N150), Ubuntu 24.04 LTS  
**Роль:** Docker host для всего prod-стэка проекта BinanceDataCollector  
**Внешний доступ:** только через Cloudflare Tunnel (без проброса портов на роутере)

> Связанные документы:
> - `docs/prod/ARCHITECTURE_PROD.md` — общая архитектура прода и состав сервисов.
> - `docs/prod/04_deployment.md` — CI/CD и процесс деплоя.
> - `docs/prod/05_setup.md` — первичная настройка чистого сервера.
> - `docs/TECH_DEBT.md` — известные проблемы инфраструктуры.

Стэк, который крутится на сервере (см. `docker/compose/docker-compose.prod.yml`):
`traefik`, `cloudflared`, `bdc_db`, `bdc_pgbouncer`, `bdc_rabbitmq`, `bdc_seq`,
`bdc_worker`, `bdc_datamanager`, `uptime_kuma`.

---

## 1. Настройка Firewall (UFW)

Мы перешли от политики «разрешено всё» к политике «разрешено только доверенное». Это решает проблемы с безопасностью и connectivity между контейнерами.

**Политика по умолчанию:**
*   **Incoming:** DENY (Запрещено)
*   **Outgoing:** ALLOW (Разрешено)

### Активные правила

| Сервис / Назначение | Порт / IP | Протокол | Комментарий | Описание |
| :--- | :--- | :--- | :--- | :--- |
| **SSH** | `2237` | TCP | `SSH Custom Port` | Доступ к терминалу сервера. Стандартный порт 22 закрыт. |
| **Local LAN** | `192.168.0.0/24` | Any | `Home Local Network` | Полный доступ с домашних устройств (Wi-Fi/LAN), включая Cockpit (9090) и другие админки. |
| **Docker Internal** | `172.16.0.0/12` | Any | `Docker Internal Traffic` | **Критично.** Разрешает контейнерам общаться друг с другом через виртуальные мосты. Без этого RabbitMQ недоступен для воркеров (`Connection refused`). |
| **Tailscale P2P** | `41641` | UDP | `Tailscale Direct` | Для прямого соединения (P2P) между узлами VPN, минуя DERP-реле. |
| **Tailscale Network** | `100.64.0.0/10` | Any | `Tailscale Network` | Полный доступ к сервисам через VPN интерфейс `tailscale0`. |
| **Cloudflare** | *См. список IP* | Any | `CF IP Range ...` | Разрешает входящий трафик от серверов Cloudflare. Критично для работы сайта при смене IP (Wi-Fi <-> Кабель). |

### Зачем эти правила

- **`2237/tcp`** — кастомный SSH-порт. Стандартный 22 закрыт, см. `docs/prod/05_setup.md` (раздел 2.1).
- **`192.168.0.0/24`** — домашняя локалка. Нужна, чтобы с ноутбука/ПК ходить на сервер по локальному IP (включая Cockpit, прямой `docker compose ps` через SSH из локалки и т.п.).
- **`172.16.0.0/12`** — диапазон Docker bridge-сетей. **Без этого правила контейнеры теряют связность через UFW** (Worker не может достучаться до RabbitMQ/Postgres/Seq, в логах будет `Connection refused`). UFW по умолчанию режет трафик между bridge-интерфейсами.
- **`41641/udp` + `100.64.0.0/10`** — Tailscale: первый порт нужен для P2P-соединений (минуя DERP-реле), второй открывает доступ ко всем сервисам через VPN-интерфейс `tailscale0`. См. раздел 3.
- **Cloudflare-диапазоны** — для входящего туннельного трафика. Cloudflare Tunnel проксирует HTTPS на сервер именно с этих подсетей. Без правил при смене провайдера/маршрутизатора `cloudflared` начинает биться об UFW.

### Список IP-диапазонов Cloudflare (Allow List)

Эти подсети добавлены в Allow, чтобы туннель не блокировался фаерволом. Список поддерживается отдельно — актуальную версию можно сверить с официальным фидом Cloudflare:

*   `198.41.128.0/17`
*   `173.245.48.0/20`
*   `103.21.244.0/22`
*   `103.22.200.0/22`
*   `103.31.4.0/22`
*   `141.101.64.0/18`
*   `108.162.192.0/18`
*   `190.93.240.0/20`
*   `188.114.96.0/20`
*   `197.234.240.0/22`
*   `162.158.0.0/15`
*   `104.16.0.0/12`
*   `172.64.0.0/13`
*   `131.0.72.0/22`

---

## 2. Автоматизация смены сети (NetworkManager Dispatcher)

**Проблема:** При переключении с Wi-Fi на Кабель меняется интерфейс и IP шлюза.
Контейнер `cloudflared` (см. `docker-compose.prod.yml`, имя контейнера —
`cloudflared`, не системный сервис) теряет связь и висит (ошибка 1033),
пока его не перезапустят.

**Решение:** Скрипт, который автоматически перезапускает контейнер туннеля
при поднятии любого физического интерфейса. Актуально для GMKtec G2:
сервер стоит дома и периодически меняет канал связи с роутером.

**Файл:** `/etc/NetworkManager/dispatcher.d/99-restart-cloudflared`

**Права:** `755` (chmod +x)

```bash
# Даём права
sudo chmod +x /etc/NetworkManager/dispatcher.d/99-restart-cloudflared

# Проверка владельца
sudo chown root:root /etc/NetworkManager/dispatcher.d/99-restart-cloudflared
```

**Содержимое скрипта:**

```bash
#!/bin/bash

INTERFACE=$1
STATUS=$2

# Логируем вызов
logger "NM-Dispatcher triggered: $INTERFACE is $STATUS"

# Игнорируем виртуальные интерфейсы Docker и локальную петлю, 
# чтобы избежать бесконечного цикла рестартов.
if [[ "$INTERFACE" == *"docker"* ]] || [[ "$INTERFACE" == *"br-"* ]] || [[ "$INTERFACE" == *"veth"* ]] || [[ "$INTERFACE" == "lo" ]]; then
    exit 0
fi

# Если физическая сеть поднялась (up или vpn-up)
if [ "$STATUS" = "up" ] || [ "$STATUS" = "vpn-up" ]; then
    logger "Network is UP ($INTERFACE). Restarting Cloudflared..."
    
    # Жесткий перезапуск контейнера
    /usr/bin/docker restart cloudflared || logger "Failed to restart cloudflared"
fi
```

---

## 3. Tailscale

Tailscale используется как admin-VPN для доступа к серверу с ноутбука вне дома
и для прямого подключения к Postgres из DBeaver.

- **Tailscale IP сервера:** `100.96.120.16`
- **Используется для:**
  - SSH-доступа (`ssh prod` — алиас, описан в `~/.ssh/config` ноутбука).
  - Прямого подключения DBeaver к Postgres: `100.96.120.16:5432`. Это
    единственная легальная точка прямого доступа к БД — изнутри LAN порт
    `5432` забинден **только** на этот Tailscale-IP (см. `docker-compose.prod.yml`).
  - Доступа к Cockpit (если включён): `https://100.96.120.16:9090`.

**Команды:**

```bash
# Статус узлов в tailnet
tailscale status

# IP-адреса этого узла (IPv4 + IPv6)
tailscale ip

# Поднять/опустить Tailscale-интерфейс на этом узле
sudo tailscale up
sudo tailscale down
```

> ⚠️ Tailscale IP `100.96.120.16` **захардкожен в `docker-compose.prod.yml`** —
> на нём биндится `5432` контейнера `bdc_db`:
> `ports: - "100.96.120.16:5432:5432"`. Если IP в Tailscale изменится
> (например, после re-auth или пересоздания узла), `docker compose up`
> упадёт с ошибкой биндинга. Зафиксировано в `docs/TECH_DEBT.md`.

---

## 4. Docker-сети и порты

### Сети

| Сеть | Тип | Кто подключён | Назначение |
| :--- | :--- | :--- | :--- |
| `internal_network` | bridge, external | Postgres, PgBouncer, RabbitMQ, Seq, Worker, DataManager | Внутренняя связь сервисов. Наружу не выходит. |
| `binancecollector_web` | external | Traefik, Cloudflare Tunnel, Seq, Uptime Kuma, Worker, DataManager | Единственная точка входа HTTP(S) — только через Traefik. |

Обе сети создаются **вручную один раз** и не пересоздаются Compose'ом.

### Порты

| Сервис | Порт | Где доступен | Комментарий |
| :--- | :--- | :--- | :--- |
| Traefik | 80 / 443 | Интернет (через Cloudflare Tunnel) | Точки входа HTTP/HTTPS |
| Traefik | 8080 | Сервер | Дашборд |
| Postgres | 5432 | **только Tailscale IP** `100.96.120.16` | Осознанное исключение — для DBeaver. Единственная точка прямого доступа к БД. |
| PgBouncer | 6432 | Сервер | Пул подключений |
| RabbitMQ | 5672 | internal_network | AMQP |
| RabbitMQ | 15672 | **не публикуется** | Management UI намеренно закрыт |
| Seq | 5341 | internal_network | Приём логов (Serilog) |
| Seq | 80 | через Traefik | UI |
| Uptime Kuma | 3001 | через Traefik | UI |

Health-эндпоинты Worker'а и DataManager'а (`/health/live`, `/health/ready`) доступны только через Traefik — их опрашивают Traefik и Uptime Kuma.

**Запрещено:** публиковать сервисы в обход Traefik и добавлять новые published-порты без записи в этой таблице.

---

## 5. Команды быстрой диагностики

Все команды read-only, ничего не меняют. Запускать на самом сервере (после `ssh prod`).

```bash
# --- UFW ---
sudo ufw status numbered
sudo ufw status verbose

# --- Активные слушающие порты ---
sudo ss -tlnp
# Только интересные порты стэка (SSH/Postgres/PgBouncer/RabbitMQ/Seq/HTTP/Cockpit)
sudo ss -tlnp | grep -E ':(2237|5432|6432|5672|15672|5341|80|443|8080|9090)'

# --- Docker сети ---
docker network ls
docker network inspect binancecollector_web
docker network inspect internal_network

# --- Контейнеры и проброс портов ---
cd /opt/BinanceCollector/docker/compose
docker compose ps
docker port bdc_db          # должен быть забинден на 100.96.120.16:5432
docker port bdc_pgbouncer   # 6432 на всех интерфейсах
docker port traefik         # 80, 443, 8080

# --- Tailscale ---
tailscale status
tailscale ip

# --- Cloudflare Tunnel: статус последних логов ---
docker logs cloudflared --tail 50

# --- Fail2Ban (что забанил по SSH) ---
sudo fail2ban-client status sshd

# --- Системные сетевые интерфейсы ---
ip addr show
ip route show
```

**Что фактически слушает хост-машина** (по `docker-compose.prod.yml`):

| Порт хоста | Куда биндится | Куда ведёт                                 |
|------------|---------------|---------------------------------------------|
| `2237/tcp` | все интерфейсы | SSH демон (системный, не Docker)            |
| `80/tcp`   | все интерфейсы | `traefik` — HTTP entrypoint                 |
| `443/tcp`  | все интерфейсы | `traefik` — HTTPS entrypoint                |
| `8080/tcp` | все интерфейсы | `traefik` — внутренний дашборд (`api.insecure=true`) |
| `5432/tcp` | **только** `100.96.120.16` (Tailscale) | `bdc_db` (PostgreSQL)              |
| `6432/tcp` | все интерфейсы | `bdc_pgbouncer` (connection pool)           |

Остальные сервисы (`bdc_rabbitmq`, `bdc_seq`, `bdc_worker`, `bdc_datamanager`,
`uptime_kuma`, `cloudflared`) портов на хост **не пробрасывают** — они доступны
только через Docker-сети `binancecollector_web`/`internal_network` или через
Traefik по доменам `*.jahasim.com`.

---

## 6. UFW: дополнительные команды

```bash
# Просмотр всех правил с номерами
sudo ufw status numbered

# Удалить правило по номеру
sudo ufw delete <номер>

# Перезагрузить правила без перезапуска сервиса
sudo ufw reload

# Включить / выключить
sudo ufw enable
sudo ufw disable

# Журнал блокировок в реальном времени
sudo journalctl -f -u ufw
sudo tail -f /var/log/ufw.log
```

---

## 7. Известные проблемы

Сетевые / инфраструктурные проблемы и долги — в `docs/TECH_DEBT.md`.
Из относящихся к этому документу:

- Tailscale IP `100.96.120.16` захардкожен в `docker-compose.prod.yml`
  (биндинг порта Postgres). Уязвимое место — при смене IP compose сломается.
