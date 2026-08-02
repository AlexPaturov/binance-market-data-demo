# Запуск demo на Linux

От чистой машины до открытого `http://localhost:7002`.

## Предусловия

1. Docker Engine + Docker Compose v2 (или Docker Desktop).
2. Docker доступен без `sudo` (пользователь в группе `docker`) — иначе запускай скрипты через `sudo`.
3. Проект на машине — через `git clone` либо скопированной папкой.

## Запуск

```bash
./docker/demo-start.sh
```

Скрипт создаёт `.env` из `.env.example`, собирает образы (первый раз — несколько минут), поднимает стек, дожидается готовности и открывает браузер.

## Вход

На странице входа выбери роль: **Viewer** (просмотр), **Operator** (+ операции), **Admin** (+ Hangfire). В БД предзагружен срез BTCUSDT за февраль 2026 — сразу видны график, панель Months и Data Quality.

## Остановка

```bash
./docker/demo-stop.sh            # остановить, данные сохранить
./docker/demo-stop.sh -v         # + удалить тома (seed перезагрузится при старте)
./docker/demo-stop.sh --purge    # + удалить тома и собранные demo-образы
```

## Если что-то пошло не так

1. **`permission denied ... docker.sock`** — добавь пользователя в группу: `sudo usermod -aG docker $USER`, перелогинься; либо запускай через `sudo`.
2. **Браузер открывает `https://localhost:7002` и ошибку** — HSTS-кеш от прежних запусков. Зайди по `http://localhost:7002` в приватном окне или очисти HSTS (Chrome: `chrome://net-internals/#hsts` → Delete `localhost`).
3. **Порт 7002 занят** — освободи порт или останови конфликтующий процесс (`ss -ltnp | grep 7002`).
