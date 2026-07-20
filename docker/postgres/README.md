# Схема БД: baseline, миграции, инструменты

Модель и обоснование — [ADR 0013](../../docs/adr/0013-schema-baseline-and-migration-automation.md).

## Раскладка

```
init/                         ← docker-entrypoint-initdb.d, порядок = цифра в имени (не версия)
├── 01_create_jobs_db.sql       создать market_analytics_jobs
├── 02_baseline.sql             канонический pg_dump всей схемы + schema_migrations (сид)
├── 03_hangfire_schema.sql      схема Hangfire (дамп с прода)
└── 04_tablespace_and_cron.sql  tablespace cold, extension pg_cron, расписание эвакуации
migrations/                   ← активные (несвёрнутые) миграции — их стережёт CI
└── archive/                    свёрнутые в baseline, читаемая нумерованная история
migrate.sh                    ← идемпотентный раннер по журналу schema_migrations
regen-schema.sh               ← перегенерация baseline (единственный способ его обновить)
check-schema-drift.sh         ← CI-страж: baseline обязан включать все активные миграции
```

`init/` применяется **только на пустом томе**. Живую БД (dev/прод) двигает `migrate.sh`.

## Учёт

Таблица `public."schema_migrations"` (`Version`/`AppliedAt`/`Checksum`) в самой БД — что уже накатано. `migrate.sh` применяет к цели только версии, которых там нет. Один источник правды про состояние, отдельно от baseline-снимка.

## Как изменить схему

1. Написать миграцию `migrations/NNN_описание.sql` (идемпотентно: `IF NOT EXISTS` / `CREATE OR REPLACE` / `IF EXISTS`). Заведомо долгую операцию (`CREATE INDEX CONCURRENTLY`) пометить в первых строках `-- @migrate-no-timeout`.
2. Свернуть в baseline: `bash regen-schema.sh` (применит миграцию в одноразовом контейнере, снимет `pg_dump`, перенесёт файл в `archive/`).
3. Закоммитить миграцию (уже в `archive/`) вместе с обновлённым `init/02_baseline.sql`.
4. CI-страж (`build-and-test`) проверит, что baseline включает все активные миграции.
5. Накатить на прод — workflow **`migrate.yml`** (`workflow_dispatch`, ввод `APPLY`), по кнопке, когда готов.

## Инструменты

| Скрипт | Зачем | Где гоняется |
| :--- | :--- | :--- |
| `migrate.sh` | накат непринятого по журналу, с `lock_timeout`/`statement_timeout` | внутри контейнера БД (прод/dev), в CI, в regen |
| `regen-schema.sh` | baseline + активные миграции → новый `02_baseline.sql` | локально разработчиком (нужен Docker) |
| `check-schema-drift.sh` | `baseline == baseline + миграции`? иначе красный | CI (`build-and-test`) |

Переменные `migrate.sh`: `MIGRATIONS_DIR`, `MIGRATE_LOCK_TIMEOUT` (5s), `MIGRATE_STATEMENT_TIMEOUT` (30min). Соединение — стандартные `PG*` (на проде — юзер через unix-сокет внутри `bdc_db`).

## Первый прогон на существующей БД (bootstrap)

Если БД уже содержит схему, но `schema_migrations` там нет, её надо создать и засидить уже применёнными версиями — иначе `migrate.sh` счёл бы БД пустой и переприменил историю. Для прода это сделано разово (см. ADR 0013). Свежий том получает `schema_migrations` из `02_baseline.sql` автоматически.
