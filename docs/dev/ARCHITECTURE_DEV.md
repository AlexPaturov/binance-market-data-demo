# Документация: ARCHITECTURE_DEV — DEV-окружение

Этот документ описывает физическую и сетевую модель **dev-окружения** проекта `BinanceDataCollector`.

Цель: одной страницей показать, **где что крутится**, **по каким адресам сервисы доступны** и **как .NET-приложения цепляются к инфраструктуре** при разработке.

> **DEV ≠ зеркало прода.** Это сознательное решение. В DEV нет Cloudflare Tunnel, Traefik, Let's Encrypt и доменов `*.jahasim.com`. Зеркалом прода будет отдельное окружение **test/staging** (его создадим позже). В DEV инфраструктура (Postgres, PgBouncer, RabbitMQ, Seq) живёт **в Docker внутри VirtualBox-VM**, а Worker и DataManager запускаются **из IDE на Windows-хосте** как обычные .NET-процессы.

---

## 1. Физическая инфраструктура

```
+-------------------------------------------------------------------+
|  Windows host (рабочая машина разработчика)                       |
|                                                                   |
|   +---------------------------+                                   |
|   |  IDE (Rider / VS)         |                                   |
|   |   - bdc_worker      :7001 |   .NET-процессы на Windows        |
|   |   - bdc_datamanager :7002 |                                   |
|   +-------------┬-------------+                                   |
|                 │ TCP по Host-Only сети (192.168.56.0/24)         |
|                 ▼                                                 |
|   +-----------------------------------------------------------+   |
|   |  VirtualBox VM (Ubuntu 20.04, headless)                   |   |
|   |                                                           |   |
|   |  Адаптер 1: Host-Only — enp0s3, IP 192.168.56.101         |   |
|   |  Адаптер 2: NAT       — enp0s8 (только для apt/docker pull)|  |
|   |                                                           |   |
|   |  Shared Folder VirtualBox:                                |   |
|   |    Windows: C:\Work\PrersonalProjects\BinanceDataCollector|   |
|   |       VM:  /mnt/bdc  (auto-mount, permanent)              |   |
|   |                                                           |   |
|   |  Docker:                                                  |   |
|   |   - bdc_db (Postgres 16)  :5432                           |   |
|   |   - bdc_pgbouncer         :6432                           |   |
|   |   - bdc_rabbitmq          :5672 / :15672                  |   |
|   |   - bdc_seq               :5341                           |   |
|   +-----------------------------------------------------------+   |
+-------------------------------------------------------------------+
```

- **Хост:** Windows 11. На нём только IDE, .NET-приложения и SSH-клиент.
- **VM:** VirtualBox с **Ubuntu 20.04**. Запускается в **headless-режиме** (правый клик в VirtualBox → *Headless Start*).
- **Docker** установлен **внутри VM**. Все контейнеры с инфраструктурой поднимаются там.
- **Worker / DataManager** в DEV **в Docker не запускаются**. Они стартуют из IDE на Windows.

---

## 2. Сетевая модель VM (два адаптера)

У VM два сетевых адаптера, каждый со своей задачей:

| # | Тип        | Интерфейс | IP                        | Назначение                                       |
|---|------------|-----------|---------------------------|--------------------------------------------------|
| 1 | Host-Only  | `enp0s3`  | `192.168.56.101` (DHCP)   | Связь Windows ↔ VM (SSH, доступ к Docker-портам) |
| 2 | NAT        | `enp0s8`  | назначается NAT-сервисом  | Выход в интернет (`apt`, `docker pull`)          |

Конфигурация netplan (внутри VM):

```yaml
# /etc/netplan/00-installer-config.yaml
network:
  version: 2
  ethernets:
    enp0s3:
      dhcp4: true
    enp0s8:
      dhcp4: true
```

**Почему так, а не Bridged:**

- Host-Only IP стабильный, не зависит от Wi-Fi/LAN роутера.
- NAT даёт интернет VM для пакетов, не открывая её наружу.
- VM не "светится" в локальной сети роутера.

---

## 3. Shared Folder (исходники проекта)

Папка проекта на Windows примонтирована в VM как Shared Folder VirtualBox:

| Сторона  | Путь                                                          |
|----------|---------------------------------------------------------------|
| Windows  | `C:\Work\PrersonalProjects\BinanceDataCollector`              |
| VM       | `/mnt/bdc`                                                    |

Параметры монтирования: **auto-mount + permanent**. Пользователь VM (`lex`) состоит в группе `vboxsf` для доступа.

**Что это даёт:**

- Compose-файлы и `.env` редактируются на Windows и **сразу видны в VM** без `scp` / `git pull`.
- В VM достаточно `cd /mnt/bdc/docker/compose && docker compose ... up -d`.
- Старый путь `/opt/BinanceCollector` на VM **больше не используется** (был удалён как пустой).

---

## 4. SSH-доступ

В `~/.ssh/config` на Windows настроены два хоста:

```
Host dev
    HostName 192.168.56.101
    Port 2237
    User lex
    IdentityFile ~/.ssh/id_ed25519

Host prod
    HostName 100.96.120.16    # Tailscale IP
    Port 2237
    User <prod-user>
    IdentityFile ~/.ssh/id_ed25519
```

Подключение: `ssh dev` или `ssh prod`.

---

## 5. Адреса сервисов

С Windows-хоста все обращения к инфраструктуре идут на Host-Only IP виртуалки.

| Сервис             | Где запущен        | Адрес для приложений (с Windows-хоста) |
|--------------------|--------------------|----------------------------------------|
| Postgres           | Docker внутри VM   | `192.168.56.101:5432` (напрямую — только для DBeaver) |
| PgBouncer          | Docker внутри VM   | `192.168.56.101:6432`                  |
| RabbitMQ (AMQP)    | Docker внутри VM   | `192.168.56.101:5672`                  |
| RabbitMQ (UI)      | Docker внутри VM   | `http://192.168.56.101:15672`          |
| Seq                | Docker внутри VM   | `http://192.168.56.101:5341`           |
| Worker (HTTP)      | IDE на Windows     | `http://localhost:7001`                |
| Hangfire Dashboard | IDE на Windows     | `http://localhost:7001/hangfire`       |
| DataManager (HTTP) | IDE на Windows     | `http://localhost:7002`                |

`localhost` валиден **только для Worker и DataManager**, потому что они физически работают на Windows-хосте.

---

## 6. Базы данных

В Postgres-контейнере работают **две** независимых базы (как и в проде):

- **`market_analytics`** — основная бизнес-БД (трейды, OHLCV, tracked symbols).
- **`market_analytics_jobs`** — служебная БД для Hangfire (очереди, расписания, состояния джобов).

Раньше Hangfire в DEV жил в той же БД, что и бизнес-данные — это создавало расхождение с прод-структурой. Сейчас в `docker-compose.dev.yml` строка `HangfireConnection` явно указывает на `market_analytics_jobs`.

---

## 7. Connection strings (`appsettings.Development.json`)

Worker и DataManager стартуют **на Windows**, а БД и брокер — **в VM**. Поэтому в строках подключения указывается Host-Only IP VM, а не `localhost`.

```json
{
  "ConnectionStrings": {
    "DefaultConnection":  "Host=192.168.56.101;Port=6432;Database=market_analytics;Username=...;Password=...",
    "HangfireConnection": "Host=192.168.56.101;Port=6432;Database=market_analytics_jobs;Username=...;Password=..."
  },
  "RabbitMQ": {
    "HostName": "192.168.56.101",
    "Port": 5672,
    "UserName": "...",
    "Password": "..."
  },
  "Serilog": {
    "WriteTo": [
      { "Name": "Seq", "Args": { "serverUrl": "http://192.168.56.101:5341" } }
    ]
  }
}
```

---

## 8. Запуск инфраструктуры

1. Стартуем VM в headless-режиме из VirtualBox.
2. `ssh dev` → `cd /mnt/bdc/docker/compose`.
3. Поднимаем нужный набор сервисов из `docker/docs/README_DOCKER_RUN.md` (для DEV из IDE — секция «Запуск ТОЛЬКО инфраструктуры»: `db.yml` + `rabbit.yml` + `seq.yml`).
4. На Windows запускаем Worker и DataManager из IDE.

---

## 9. Что нельзя делать в DEV

- Использовать `docker-compose.prod.yml` — он рассчитан на Traefik / Cloudflare Tunnel и сломает локальный запуск.
- Считать, что Worker/DataManager доступны изнутри VM по `localhost:7001/7002` — они работают на Windows-хосте.
- Хранить ценные данные в DEV-volume'ах: пересоздание VM или контейнеров — рутина.
- Трогать `docker-compose.override.yml` — оставлен как есть, разберёмся отдельной задачей.

## 10. Диагностика: VM недоступна по сети с Windows

Симптом: `ssh dev` (или подключение к портам Postgres/RabbitMQ/Seq на `192.168.56.101`) падает с ошибкой:

```
ssh: connect to host 192.168.56.101 port 2237: Connection timed out
```

При этом **внутрь VM можно зайти через консоль VirtualBox**, и `ip a` показывает, что `enp0s3` поднят и имеет IP `192.168.56.101`.

`Connection timed out` означает, что SYN-пакеты уходят, но ответов нет — либо они не доходят до VM, либо ответы теряются по дороге. Самая частая причина в Host-Only сети VirtualBox — **рассинхрон ARP/DHCP** после suspend/resume VM или после долгого простоя: Windows держит устаревший ARP-маппинг IP → MAC, и пакеты улетают "в пустоту".

### Быстрая проверка и фикс

На Windows (PowerShell, не обязательно от админа):

```powershell
# 1. Посмотреть ARP-запись для IP виртуалки
arp -a 192.168.56.101
```

Если в выводе:
- запись `incomplete`, **или**
- MAC отличается от MAC интерфейса `enp0s3` внутри VM (его смотрим командой `ip a show enp0s3` → строка `link/ether ...`)

→ это ARP-проблема. Чистим кэш:

```powershell
# 2. Удалить устаревшую ARP-запись (требует админских прав)
arp -d 192.168.56.101
```

После этого пингуем `192.168.56.101` и пробуем `ssh dev` заново — связность должна восстановиться в течение нескольких секунд.

### Если не помогло

Проверяем по порядку:

1. **Windows Defender Firewall профиль для Host-Only адаптера.** После переподключений к Wi-Fi Windows иногда переклассифицирует адаптер в `Public`, и фаервол режет трафик:

   ```powershell
   Get-NetConnectionProfile
   # Если для VirtualBox Host-Only Ethernet Adapter NetworkCategory: Public:
   Set-NetConnectionProfile -InterfaceAlias "Ethernet 3" -NetworkCategory Private
   ```
   (Имя интерфейса — то, что показывает `ipconfig` для VirtualBox Host-Only Adapter; у меня это `Ethernet 3`.)

2. **Маршрут на `192.168.56.0/24` перехвачен VPN/Tailscale.** Проверка:

   ```powershell
   Get-NetRoute -DestinationPrefix "192.168.56.0/24"
   ```
   `ifIndex` должен указывать на VirtualBox Host-Only адаптер, а не на Tailscale/VPN-интерфейс.

3. **Адаптер выключен в Windows.** `Win+R` → `ncpa.cpl` → `VirtualBox Host-Only Network` → Disable/Enable.

4. **Host-Only сеть в VirtualBox потеряла настройки** (бывает после обновления VirtualBox). VirtualBox → `File` → `Tools` → `Network Manager` → проверить, что Host-Only сеть существует, IPv4 = `192.168.56.1/24`, DHCP включён и его диапазон включает `.101`.

### Профилактика

Если проблема повторяется регулярно после suspend/resume VM — проще зафиксировать IP `192.168.56.101` статически в netplan VM (вместо DHCP), это уберёт класс проблем с переарендой адресов.