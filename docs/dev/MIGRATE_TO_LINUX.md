# Переезд dev-окружения с Windows на Ubuntu

Переход от схемы **Windows + VirtualBox VM** к **Ubuntu как хосту**. Docker и .NET-приложения работают прямо на Ubuntu — VM больше не нужна.

---

## 1. Подготовка Ubuntu-хоста

- [ ] Установить Docker + Docker Compose plugin (`apt install docker.io docker-compose-plugin` или официальный способ через apt.docker.com)
- [ ] Добавить пользователя в группу docker: `sudo usermod -aG docker $USER`
- [ ] Установить .NET 8 SDK (Microsoft packages feed для Ubuntu)
- [ ] Установить Rider через JetBrains Toolbox (`toolbox.jetbrains.com`)
- [ ] Установить `git`, настроить SSH-ключ для GitHub

---

## 2. Перенос данных и диски

Postgres-данные сейчас на 4TB USB-C диске `/mnt/ext`. На новом хосте диск подключается напрямую — никакого USB Passthrough через VirtualBox.

- [ ] Подключить 4TB диск, убедиться что он виден: `lsblk`
- [ ] Найти UUID: `sudo blkid /dev/sdX1`
- [ ] Добавить в `/etc/fstab` с флагом `nofail`:
  ```
  UUID=<uuid> /mnt/ext ext4 defaults,nofail,x-systemd.device-timeout=10s 0 2
  ```
- [ ] Проверить: `sudo mount -a && ls /mnt/ext/postgres_data`
- [ ] Создать systemd override чтобы Docker ждал монтирования диска — **это фикс главной проблемы "каждый ребут ломает всё"**:
  ```bash
  sudo mkdir -p /etc/systemd/system/docker.service.d
  sudo tee /etc/systemd/system/docker.service.d/override.conf <<EOF
  [Unit]
  RequiresMountsFor=/mnt/ext
  EOF
  sudo systemctl daemon-reload
  ```
  После этого: если `/mnt/ext` не примонтирован — Docker не стартует вообще (явная ошибка), а не стартует с пустым data dir.

---

## 3. Репозиторий

На Windows проект лежит в `C:\Work\PrersonalProjects\BinanceDataCollector`, на VM монтировался как Shared Folder в `/mnt/bdc`. На Ubuntu просто клонируем куда удобно.

- [ ] `git clone git@github.com:<repo>.git ~/projects/BinanceDataCollector`
- [ ] Shared Folder VirtualBox больше не нужен — в docker-compose.dev.yml bind mount `../../src:/app/src` будет работать из реального пути

---

## 4. Изменения в docker-compose

### 4.1. Создание базы `market_analytics_jobs`

`POSTGRES_DB` создаёт только одну БД. `market_analytics_jobs` нужно создать через init-скрипт — тогда она будет создаваться автоматически при первом старте Postgres (пока data dir пустой).

- [ ] Создать файл `docker/postgres/init/01_create_jobs_db.sql`:
  ```sql
  SELECT 'CREATE DATABASE market_analytics_jobs OWNER bindatacoll'
  WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'market_analytics_jobs')\gexec
  ```
- [ ] Добавить bind mount в `docker-compose.db.yml` и `docker-compose.dev.yml` для `bdc_db`:
  ```yaml
  volumes:
    - /mnt/ext/postgres_data:/var/lib/postgresql/data
    - ../../docker/postgres/init:/docker-entrypoint-initdb.d
  ```
  > `docker-entrypoint-initdb.d` выполняется только при пустом data dir — то есть при первом запуске. На существующих данных — нет.

### 4.2. Порты

В текущей схеме `192.168.56.101` — это Host-Only IP VM. На Ubuntu все сервисы будут на `localhost`. Compose-файлы менять не надо — порты пробрасываются так же. Менять нужно только `launchSettings.json`.

---

## 5. Изменения в коде / конфигах

### 5.1. `launchSettings.json` — оба проекта

Файл в `.gitignore`, создаётся вручную. На Ubuntu менять `192.168.56.101` → `localhost` везде.

**Worker** (`src/BinanceDataCollector.Worker/Properties/launchSettings.json`):
```json
{
  "profiles": {
    "WorkerProf": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_UTF8_CONSOLE": "true",
        "ConnectionStrings__DefaultConnection": "Host=localhost;Port=6432;Database=market_analytics;Username=bindatacoll;Password=dt_hfgd_yyyd",
        "ConnectionStrings__HangfireConnection": "Host=localhost;Port=6432;Database=market_analytics_jobs;Username=bindatacoll;Password=dt_hfgd_yyyd",
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

**DataManager** (`src/BinanceDataCollector.DataManager/Properties/launchSettings.json`) — то же самое, порт `7002`.

### 5.2. `appsettings.Development.json` — Worker

Путь `BasePath` сейчас `D:\Media\Downloads\Temp` — Windows-путь. На Ubuntu:

- [ ] Решить куда класть архивы и CSV (например `/mnt/ext/bdc_data` или `~/bdc_data`)
- [ ] Обновить `ArchivesSettings.BasePath` в `appsettings.Development.json`:
  ```json
  "ArchivesSettings": {
    "BasePath": "/mnt/ext/bdc_data"
  }
  ```
- [ ] Создать директорию: `mkdir -p /mnt/ext/bdc_data/Trades/Downloaded /mnt/ext/bdc_data/Trades/Unpacked`

### 5.3. `appsettings.Development.json` — DataManager

Проверить `ArchivesSettings.BasePath` — аналогично Worker, если используется.

---

## 6. SSH-конфиг

На Windows `~/.ssh/config` имел запись `dev` → `192.168.56.101`. На Ubuntu:

- [ ] Удалить или закомментировать запись `Host dev` (VM больше нет)
- [ ] Оставить `Host prod` (Tailscale IP — без изменений)

---

## 7. Обновить документацию

- [ ] Переписать `docs/dev/ARCHITECTURE_DEV.md` под схему Ubuntu (убрать VirtualBox, Shared Folder, Host-Only сеть, `192.168.56.101`)
- [ ] Обновить таблицу адресов сервисов: всё → `localhost`
- [ ] Обновить `docker/docs/README_DOCKER_RUN.md` — команды запуска без упоминания VM

---

## 8. Проверка после переезда

- [ ] `docker compose ... up -d` поднимает инфраструктуру без ошибок
- [ ] `docker exec bdc_db psql -U bindatacoll -l` показывает обе БД: `market_analytics` и `market_analytics_jobs`
- [ ] Worker запускается из Rider, коннектится к Postgres и RabbitMQ
- [ ] DataManager запускается, Hangfire Dashboard открывается на `http://localhost:7002/hangfire`
- [ ] После ребута: `mount | grep ext` показывает `/mnt/ext`, Docker стартует, обе БД на месте
