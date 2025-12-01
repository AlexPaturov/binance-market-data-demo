# Docker Environment – Run Guide

Этот документ содержит команды запуска для различных конфигураций Docker окружения BinanceDataCollector.

---
## 🧪 1. Запуск полного DEV окружения
Сборка и запуск: Postgres + PgBouncer + RabbitMQ + Seq + Worker + DataManager

```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.dev.yml \
  up --build
```

Остановить:
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.dev.yml \
  down
```

---
## 🗄 2. Запуск только базы данных (для DBeaver)
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.db.yml \
  up -d
```

---
## 🐇 3. Запуск только RabbitMQ
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.rabbit.yml \
  up -d
```

Остановка:
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.rabbit.yml \
  down
```

---
## 📊 4. Запуск только Seq (логов)
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.seq.yml \
  up -d
```

---
## 🚀 5. Запуск полной PROD инфраструктуры (сервер)
Эта команда запускает Traefik + Cloudflare Tunnel + PgBouncer + Postgres + RabbitMQ + Seq + Worker + DataManager.

```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.prod.yml \
  up -d
```

Остановка PROD:
```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.prod.yml \
  down
```

---
## 🧹 Очистка зависших контейнеров/томов
Если что-то пошло не так:

```bash
docker compose down -v --remove-orphans
```

---
## 🔍 Проверка итоговой конфигурации
Перед запуском можно увидеть итоговую композицию:

```bash
docker compose \
  -f compose/docker-compose.yml \
  -f compose/docker-compose.dev.yml \
  config
```

---

## Ошибки при запуске
- **ERR: service has neither an image nor build context** — значит забыли `image:` или `build:` в секции сервиса.
- **Bind for 0.0.0.0:PORT failed** — порт занят локальным процессом.
- **Environment variable not set** — отсутствует переменная в `.env`.
- **Network not found** — запуск без базового compose-файла.

## Как дебажить контейнеры
- Посмотреть логи: `docker logs <container>`
- Войти внутрь контейнера: `docker exec -it <container> sh`
- Проверить сетевые связи: `docker exec <c> ping <other>`
- Проверить переменные окружения: `docker exec <c> printenv`
- Проверить структуру файлов: `docker exec <c> ls -R /app`

## Как смотреть логи всех сервисов сразу
- Запустить tail всех контейнеров: `docker compose logs -f`
- Запустить tail для одного: `docker compose logs -f <service>`
- Последние 50 строк: `docker logs <container_name> --tail=50`
- Следить за логами в реальном времени + последние 50 строк `docker logs -f --tail=50 <container_name>`
- Только ошибки и warnings `docker logs <container_name> 2>&1 | egrep -i "err|fail|warn|critical"`
- Перезапустить DataManager и сразу ловить свежие старт-логи `docker restart <container_name> && sleep 2 && docker logs <container_name> --timestamps --tail=200`
- Остановка всех контейнеров `docker stop $(docker ps -q)`
- Удаление всех контейнеров `docker rm $(docker ps -aq)`
- Удалить конкретный том `docker volume rm <volume_name>`
- Список всех сетей `docker network ls`
- Останови контейнер `docker stop <container_name>`
- Удалить контейнер `docker rm <container_name>`
- Смотрим образы `docker images`
- Смотрим тома `docker volume ls`
- Cмотрим сети конкретного контейнера `docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{println $k}}{{end}}' <container_name>`

## Как чистить тома, сети, dangling images
- Удалить остановленные контейнеры: `docker container prune`
- Удалить неиспользуемые образы: `docker image prune -a`
- Удалить неиспользуемые сети: `docker network prune`
- Удалить dangling volumes: `docker volume prune`
- Удалить **только том проекта**: `docker volume rm <project>_<volume>`

## Работа с .env и secrets
- Все переменные должны быть определены в `.env`
- Никогда не комить `.env` в репозиторий
- На проде лучше использовать Docker secrets или переменные окружения сервера
- Проверить подгрузку `.env`: `docker compose config`

