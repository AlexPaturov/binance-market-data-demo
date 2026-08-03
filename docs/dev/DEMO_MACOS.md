# Запуск demo на macOS

От чистой машины до открытого `http://localhost:7002`. Работает на Apple Silicon (arm64) и Intel — все образы мультиплатформенные.

## Предусловия

1. **Docker Desktop for Mac** установлен и запущен (в меню-баре — кит, статус «Docker Desktop is running»).
   Установка при необходимости: `brew install --cask docker`, затем запусти Docker Desktop из Launchpad и дождись готовности.
2. Проект на машине — через `git clone` либо скопированной папкой.

## Запуск

```bash
./docker/demo-start.sh
```

Если папку копировали (не `git clone`), у скрипта мог потеряться флаг исполнения — тогда `permission denied`. Запусти через `bash ./docker/demo-start.sh` либо верни флаг: `chmod +x docker/demo-start.sh docker/demo-stop.sh`. `sudo` не нужен.

Скрипт создаёт `.env` из `.env.example`, собирает образы (первый раз — несколько минут), поднимает стек, дожидается готовности и открывает браузер (`open`).

## Вход

На странице входа выбери роль: **Viewer** (просмотр), **Operator** (+ операции), **Admin** (+ Hangfire). В БД предзагружен срез BTCUSDT за февраль 2026 — сразу видны график, панель Months и Data Quality.

## Остановка

```bash
./docker/demo-stop.sh            # остановить, данные сохранить
./docker/demo-stop.sh -v         # + удалить тома (seed перезагрузится при старте)
./docker/demo-stop.sh --purge    # + удалить тома и собранные demo-образы
```

## Если что-то пошло не так

1. **`Cannot connect to the Docker daemon`** — Docker Desktop не запущен. Запусти его из Launchpad, дождись «Docker Desktop is running», повтори.
2. **Сборка падает по нехватке памяти** — в Docker Desktop → Settings → Resources подними лимит памяти (демо комфортно при 4 ГБ+).
3. **Браузер открывает `https://localhost:7002` и ошибку** — HSTS-кеш от прежних запусков. Зайди по `http://localhost:7002` в приватном окне или очисти HSTS (Chrome: `chrome://net-internals/#hsts` → Delete `localhost`; Safari — очисти данные сайта для localhost).
4. **Порт 7002 занят** — освободи порт или останови конфликтующий процесс (`lsof -i :7002`).
