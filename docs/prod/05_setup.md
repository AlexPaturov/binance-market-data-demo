# Документация: 05 - Настройка Сервера (Ubuntu Server 22.04 LTS)

Этот документ описывает полный пошаговый процесс настройки "чистого" сервера на базе Ubuntu Server 22.04 LTS для развертывания приложения `BinanceDataCollector`.

## 1. Установка ОС

- **Образ:** Используется `Ubuntu Server 22.04 LTS`.
- **Ключевой момент при установке:** На шаге "SSH Setup" необходимо обязательно поставить галочку **`[X] Install OpenSSH server`**.

## 2. Усиление безопасности (Security Hardening)

Эти шаги выполняются сразу после первого входа на сервер по SSH.

### 2.1. Настройка SSH
1.  **Изменить порт по умолчанию:**
    - Открыть файл: `sudo nano /etc/ssh/sshd_config`
    - Найти строку `#Port 22`, раскомментировать и изменить на нестандартный порт (например, `Port 2237`).

2.  **Настроить аутентификацию по ключу:**
    - На рабочей машине сгенерировать ключ: `ssh-keygen -t ed25519`.
    - Скопировать **публичный** ключ на сервер:
      ```bash
      ssh-copy-id -p 2237 ваш_логин@ip_адрес
      ```

3.  **Отключить аутентификацию по паролю и вход для root:**
    - `sudo nano /etc/ssh/sshd_config`
    - Установить `PasswordAuthentication no`.
    - Установить `PermitRootLogin no`.

4.  **Перезапустить SSH для применения настроек:**
    - `sudo systemctl restart ssh`

### 2.2. Настройка Fail2Ban
1.  **Установить:** `sudo apt install fail2ban`
2.  **Создать локальный конфиг:** `sudo cp /etc/fail2ban/jail.conf /etc/fail2ban/jail.local`
3.  **Настроить порт SSH:**
    - `sudo nano /etc/fail2ban/jail.local`
    - В секции `[sshd]` изменить `port = ssh` на `port = 2237`.
4.  **Перезапустить сервис:** `sudo systemctl restart fail2ban`

## 3. Настройка сети и фаервола

### 3.1. Статический IP-адрес (на роутере)
- **Метод:** DHCP Reservation (Привязка IP и MAC).
- **Действие:** В веб-интерфейсе роутера создать правило, которое привязывает MAC-адрес сетевой карты сервера к постоянному IP-адресу (например, `192.168.0.200`). Это предпочтительнее ручной настройки `netplan` на сервере.

### 3.2. Фаервол UFW
1.  **Разрешить SSH-порт:**
    - `sudo ufw allow 2237/tcp` (для SSH)
2.  **Включить фаервол:** `sudo ufw enable`

> Порт PostgreSQL (`5432`) в UFW **не открывается**: он биндится только на Tailscale-IP, и доступ к нему из DBeaver идёт через VPN. Полный набор правил UFW (LAN, Docker, Tailscale, Cloudflare) — [`network.md §1`](./network.md#1-настройка-firewall-ufw).

## 4. Установка и настройка Docker

1.  **Установить Docker Engine и Docker Compose** по официальной инструкции.
2.  **Добавить пользователя в группу `docker`:**
    - `sudo usermod -aG docker ${USER}`
    - **Важно:** После этого необходимо **выйти из SSH и зайти снова**.
    - Проверить командой `docker ps`.

## 5. Настройка CI/CD (GitHub Actions Self-hosted Runner)

1.  **Зарегистрировать Runner:**
    - В настройках репозитория GitHub (`Settings -> Actions -> Runners`) создать новый `self-hosted runner`.
    - Выполнить на сервере все команды из сгенерированной инструкции (скачивание, распаковка, `./config.sh`).
2.  **Установить Runner как сервис:**
    - `sudo ./svc.sh install`
    - `sudo ./svc.sh start`
3.  **Проверить статус:** `sudo ./svc.sh status` (должен быть `active (running)`).

---