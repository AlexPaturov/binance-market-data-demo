# Запуск Docker-окружения

Команды запускаются **из корня проекта**.

> Прод поднимается своими скриптами — см. [`../../docs/prod/ARCHITECTURE_PROD.md`](../../docs/prod/ARCHITECTURE_PROD.md).

---

## Инфраструктура для разработки (обычный режим)

Postgres, PgBouncer, RabbitMQ, Seq. Worker и DataManager запускаются из IDE.

```bash
./docker/dev-start.sh
./docker/dev-stop.sh
```

Скрипты заодно создают каталоги под архивы и CSV. Данные Postgres — в volume `dev_postgres_data`; схема из `docker/postgres/init/` накатывается автоматически при первом старте (на пустом volume).

---

## Весь стек в контейнерах

Инфраструктура + Worker + DataManager, оба в режиме `dotnet watch run`.

```bash
docker compose -f docker/compose/docker-compose.yml \
               -f docker/compose/docker-compose.dev.yml up --build
```

Остановить — та же команда с `down`.

---

## Отдельные сервисы

Базовый файл `docker-compose.yml` обязателен всегда — в нём сети и общие настройки.

| Что | Дополнительный файл |
| :--- | :--- |
| Только Postgres + PgBouncer | `docker-compose.db.yml` |
| Только RabbitMQ | `docker-compose.rabbit.yml` |
| Только Seq | `docker-compose.seq.yml` |

```bash
docker compose -f docker/compose/docker-compose.yml \
               -f docker/compose/docker-compose.db.yml up -d
```

---

## Проверить итоговую конфигурацию

Compose-файлы накладываются друг на друга, и результат не всегда очевиден:

```bash
docker compose -f docker/compose/docker-compose.yml \
               -f docker/compose/docker-compose.dev.yml config
```

---

## Типовые ошибки

| Сообщение | Причина |
| :--- | :--- |
| `service has neither an image nor build context` | в сервисе нет ни `image:`, ни `build:` |
| `Bind for 0.0.0.0:PORT failed` | порт занят локальным процессом |
| `Environment variable not set` | переменной нет в `.env` |
| `Network not found` | запуск без базового `docker-compose.yml` |
| `external volume ... not found` | volume не создан — создать вручную `docker volume create` |

Отладка: `docker logs -f --tail=50 <container>`, `docker exec -it <container> sh`, `docker exec <container> printenv`.
