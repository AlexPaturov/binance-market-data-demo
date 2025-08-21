# Документация: 05 - Настройка Сервера (Ubuntu Server 24.04 LTS)

Этот документ описывает полный пошаговый процесс настройки "чистого" сервера на базе Ubuntu Server 22.04 LTS для развертывания приложения `BinanceDataCollector`.

## 1. Установка ОС

- **Образ:** Используется `Ubuntu Server 24.04.x LTS`.
- **Ключевой момент при установке:** На шаге "SSH Setup" необходимо обязательно поставить галочку **`[X] Install OpenSSH server`**.

## 2. Усиление безопасности (Security Hardening)

Эти шаги выполняются сразу после первого входа на сервер по SSH.

### 2.1. Настройка SSH
1.  **Изменить порт по умолчанию:**
    - Открыть файл: `sudo nano /etc/ssh/sshd_config`
    - Найти строку `#Port 22`, раскомментировать и изменить на нестандартный порт (например, `Port 2237`).

2.  **Настроить аутентификацию по ключу:**
    - **На Windows-ПК** сгенерировать ключ: `ssh-keygen`.
    - Скопировать **публичный** ключ на сервер командой в PowerShell:
      ```powershell
      type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh ваш_логин@ip_адрес -p 2237 "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys"
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
1.  **Разрешить необходимые порты:**
    - `sudo ufw allow 2237/tcp` (для SSH)
    - `sudo ufw allow 5432/tcp` (для доступа к PostgreSQL из DBeaver)
    - `sudo ufw allow 5341/tcp` (для доступа к веб-интерфейсу Seq)
2.  **Включить фаервол:** `sudo ufw enable`

## 4. Установка и настройка Docker

### 4.1. Установка Docker Engine (для Ubuntu 24.04 "Noble")
1. **Подготовка:**
   ```bash
   sudo apt-get update
   sudo apt-get install -y ca-certificates curl
   ```
2. **Добавление GPG-ключа Docker:**
   ```bash
   sudo install -m 0755 -d /etc/apt/keyrings
   sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
   sudo chmod a+r /etc/apt/keyrings/docker.asc
   ```
3. **Добавление репозитория:**
   ```bash
   echo \
     "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
     $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
     sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
   ```
4. **Установка:**
   ```bash
   sudo apt-get update
   sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
   ```
### 4.2. Настройка прав пользователя
1. **Добавить пользователя в группу `docker`:**
   ```bash
   sudo usermod -aG docker ${USER}
   ```
2. **Применить изменения:** **Выйти из SSH и зайти снова.**
3. **Проверить:** `docker ps` (должно работать без `sudo`).

## 5. Настройка CI/CD (GitHub Actions Self-hosted Runner)

1.  **Зарегистрировать Runner:**
    - В настройках репозитория GitHub (`Settings -> Actions -> Runners`) создать новый `self-hosted runner`.
    - Выполнить на сервере все команды из сгенерированной инструкции (скачивание, распаковка, `./config.sh`).
2.  **Установить Runner как сервис:**
    - `sudo ./svc.sh install`
    - `sudo ./svc.sh start`
3.  **Проверить статус:** `sudo ./svc.sh status` (должен быть `active (running)`).

---