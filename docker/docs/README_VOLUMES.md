# Хранилища и что нельзя трогать

Где физически лежат данные прода, почему именно так, и какие изменения ломают систему не сразу, а через часы.

> **Общий принцип.** Если что-то работает в проде, и вы не понимаете на 100%, зачем оно так сделано — не трогайте.

---

## Схема хранения

| Что | Где | Тип |
| :--- | :--- | :--- |
| **Данные PostgreSQL** | `/mnt/ext/postgres_data` | **bind mount** на внешний диск |
| Логи (Seq) | `binancecollector_seq_data` | external named volume |
| Очереди (RabbitMQ) | `binancecollector_rabbitmq_data` | external named volume |
| Сертификаты (Let's Encrypt) | `binancecollector_letsencrypt_data` | external named volume |
| Архивы и CSV | `binancecollector_bdc_data` | external named volume |
| Uptime Kuma | `binancecollector_uptime_kuma_data` | external named volume |

---

## PostgreSQL — внешний диск

```yaml
bdc_db:
  volumes:
    - /mnt/ext/postgres_data:/var/lib/postgresql/data
```

**Диск:** внешний 4TB, примонтирован в `/mnt/ext` через `/etc/fstab` по UUID `25ddc534-13e6-479e-8392-a4487a975c80` с опцией `nofail` (сервер грузится даже без диска).

**Почему bind mount, а не named volume.** Исторические данные занимают сотни ГБ и не помещаются на системный диск (466 ГБ). База наполнялась на dev-машине на этом же диске, после чего диск физически переставили на прод.

### ⚠️ Главная опасность

Если `bdc_db` стартует, когда **диск не примонтирован**, Docker создаст пустую директорию `/mnt/ext/postgres_data` на системном диске, и Postgres молча проинициализирует в ней **новую пустую базу**. Приложение поднимется и подключится — но таблиц не будет. Данные на самом диске при этом целы.

**Защита на двух уровнях:**

- **Автозапуск:** drop-in `/etc/systemd/system/docker.service.d/wait-for-ext.conf` с `RequiresMountsFor=/mnt/ext` — Docker не стартует, пока диск не примонтирован.
- **Ручной запуск:** `prod-start.sh` проверяет монтирование, наличие `PG_VERSION` и партиций.

**Проверить руками:**

```bash
mountpoint /mnt/ext    # /mnt/ext is a mountpoint
df -h /mnt/ext         # Size ~3.6T, а не системный раздел на 466G
```

### 🚫 Нельзя

- отмонтировать `/mnt/ext` при работающем `bdc_db`;
- менять или удалять запись в `/etc/fstab`;
- поднимать `bdc_db`, не убедившись, что диск примонтирован.

---

## Named volumes

Объявлены в `docker-compose.prod.yml` как `external: true` — Compose их **не создаёт и не удаляет**, только подключает. Создаются один раз (в CI-шаге деплоя есть `docker volume create ... || true`).

### 🚫 Нельзя

- удалять (`docker volume rm`);
- пересоздавать;
- менять `name:`;
- делать их non-external.

**Почему:** потеря данных, потеря логов, потеря сертификатов (плюс rate-limit от Let's Encrypt), неконсистентное состояние Hangfire.

### ⚠️ Права при заливке файлов извне

`bdc_worker` работает под непривилегированным пользователем (`USER app` в `docker/Dockerfile`, uid/gid **1654**). Если положить файлы в volume через сторонний контейнер, они окажутся во владении **root**, и Worker не сможет ни распаковать архив, ни удалить обработанный файл:

```
Error extracting archive XXX.zip: Access to the path
'/opt/bdc_data/Trades/Unpacked/XXX' is denied.
```

После любой такой заливки вернуть владение:

```bash
docker run --rm -v binancecollector_bdc_data:/data alpine chown -R 1654:1654 /data
```

---

## Сети

`binancecollector_web` и `internal_network` — **external**, создаются вручную. Нельзя менять имя, тип или `external: true`: ошибка в сети = Traefik перестаёт маршрутизировать, сервис исчезает из интернета.

Подробности — `docs/prod/network.md`.

---

## Порты и Traefik

- Worker и DataManager **никогда** не публикуют порты напрямую (`ports:`). Доступ только через Traefik: иначе ломается TLS, ломается Cloudflare и нарушается модель безопасности.
- Метки `traefik.http.routers.*` / `services.*` / `docker.network` — не трогать без понимания: ошибка в метке убирает сервис из интернета.

---

## Secrets

- `.env` не коммитить;
- секреты не логировать;
- не дублировать секреты в `appsettings`.

Если сервис не стартует из-за отсутствующей переменной окружения — **это нормально**, а не баг.

---

## DEV ≠ PROD

- `docker-compose.dev.yml` **не запускать** на сервере;
- dev и prod compose-файлы **не смешивать**.

---

## Перед любым изменением

Ответьте себе на три вопроса:

1. Что именно я хочу улучшить?
2. Что может сломаться?
3. Как откатиться?

Нет чёткого ответа — не меняйте.
