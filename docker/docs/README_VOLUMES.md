# README_VOLUMES

Где физически лежат данные прода и почему именно так.

---

## Схема хранения

| Что | Где | Тип |
| :--- | :--- | :--- |
| Данные PostgreSQL | `/mnt/ext/postgres_data` | **bind mount** на внешний диск |
| Логи (Seq) | `binancecollector_seq_data` | external named volume |
| Очереди (RabbitMQ) | `binancecollector_rabbitmq_data` | external named volume |
| Сертификаты (Let's Encrypt) | `binancecollector_letsencrypt_data` | external named volume |
| Архивы и CSV | `binancecollector_bdc_data` | external named volume |
| Uptime Kuma | `binancecollector_uptime_kuma_data` | external named volume |

---

## PostgreSQL — bind mount на внешний диск

```yaml
bdc_db:
  volumes:
    - /mnt/ext/postgres_data:/var/lib/postgresql/data
```

**Диск:** внешний 4TB, примонтирован в `/mnt/ext` через `/etc/fstab` по UUID `25ddc534-13e6-479e-8392-a4487a975c80` с опцией `nofail` (сервер грузится, даже если диск не подключён).

**Почему bind mount, а не named volume.** Исторические данные (тики за годы) занимают сотни ГБ и не помещаются на системный диск сервера (466 ГБ). База наполнялась на dev-машине на этом же диске, после чего диск физически переставили на прод — bind mount позволяет подключить готовые данные как есть, без копирования.

### ⚠️ Главная опасность

Если `bdc_db` стартует, когда диск **не примонтирован**, Docker создаст пустую директорию `/mnt/ext/postgres_data` на системном диске, и Postgres молча проинициализирует в ней **новую пустую базу**. Приложение поднимется и подключится — но таблиц не будет. Данные на самом диске при этом целы.

**Перед стартом `bdc_db` проверять:**

```bash
mountpoint /mnt/ext    # /mnt/ext is a mountpoint
df -h /mnt/ext         # Size ~3.6T, а не системный раздел на 466G
```

Если база уже стартовала на пустышке: остановить `bdc_db`, удалить ошибочную директорию, примонтировать диск, поднять заново.

---

## Named volumes

Все объявлены в `docker-compose.prod.yml` как `external: true` — Compose их **не создаёт и не удаляет**, только подключает. Создаются один раз (в CI-шаге деплоя есть `docker volume create ... || true`).

Удаление любого = потеря данных, логов или сертификатов (последнее — ещё и rate-limit от Let's Encrypt). См. `README_DO_NOT_TOUCH.md`.

### `binancecollector_bdc_data` — особый случай

Смонтирован в `bdc_worker` **дважды**:

```yaml
bdc_worker:
  volumes:
    - bdc_data:/opt/bdc_data
    - bdc_data:/home/lex/bdc_data
```

Второй путь нужен, потому что в очереди Hangfire остались задачи импорта CSV, поставленные ещё на dev-машине — их аргумент содержит абсолютный dev-путь (`/home/lex/bdc_data/...`), зашитый в момент постановки задачи. Один и тот же volume, две точки монтирования: старые задачи находят свои файлы, новые работают по штатному пути из конфига (`ArchivesSettings.BasePath` = `/opt/bdc_data`).

Второй mount можно убрать, когда очередь `archive_import` полностью разберётся.

---

## ⚠️ Права при заливке файлов в volume извне

`bdc_worker` в проде работает под непривилегированным пользователем (`USER app` в `docker/Dockerfile`, uid/gid **1654**). Если положить файлы в `binancecollector_bdc_data` через сторонний контейнер (типичный приём — `docker run --rm -v binancecollector_bdc_data:/dest alpine cp ...`), они окажутся во владении **root**, и Worker не сможет ни распаковать архив, ни удалить обработанный файл:

```
Error extracting archive XXX.zip: Access to the path
'/opt/bdc_data/Trades/Unpacked/XXX' is denied.
```

После любой такой заливки — вернуть владение:

```bash
docker run --rm -v binancecollector_bdc_data:/data alpine chown -R 1654:1654 /data
```

Проверка:

```bash
docker exec bdc_worker sh -c 'touch /opt/bdc_data/Trades/Unpacked/.wtest && echo WRITABLE && rm /opt/bdc_data/Trades/Unpacked/.wtest'
```
