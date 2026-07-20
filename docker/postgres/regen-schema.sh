#!/usr/bin/env bash
#
# Перегенерация baseline-снимка схемы: применяет текущий init/02_baseline.sql + непринятые
# миграции в одноразовом контейнере, снимает канонический pg_dump, дописывает сид
# schema_migrations, переносит свёрнутые миграции в migrations/archive/.
#
# Baseline руками НЕ правится — только этим скриптом. Правишь схему → пишешь миграцию в
# migrations/ → гоняешь regen-schema → коммитишь миграцию (в archive/) + новый baseline.
#
# Нужен только Docker. pg_dump/psql берутся из образа (детерминированная версия).
#
set -euo pipefail

PG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"      # docker/postgres
BASELINE="$PG_DIR/init/02_baseline.sql"
IMAGE="${REGEN_IMAGE:-bdc/postgres-cron:16}"
CTR="bdc_regen_$$"
PW="regen_$$"
SENTINEL="-- @@ schema_migrations seed (regen-schema дописывает; --schema-only данные не берёт) @@"

cleanup() { docker rm -f "$CTR" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo ">> одноразовый контейнер $IMAGE"
docker run -d --name "$CTR" \
    -e POSTGRES_USER=bindatacoll -e POSTGRES_PASSWORD="$PW" -e POSTGRES_DB=market_analytics \
    -e POSTGRES_INITDB_ARGS='--locale=C.UTF-8 --encoding=UTF8' \
    "$IMAGE" -c shared_preload_libraries=pg_cron -c cron.database_name=market_analytics >/dev/null

for _ in $(seq 1 30); do
    docker exec "$CTR" pg_isready -U bindatacoll -d market_analytics >/dev/null 2>&1 && break
    sleep 1
done

docker cp "$PG_DIR/." "$CTR:/pg"
PGENV=(-e PGPASSWORD="$PW" -e PGUSER=bindatacoll -e PGDATABASE=market_analytics)

# Baseline намеренно БЕЗ pg_cron и без tablespace 'cold': расширение и пространство
# создаёт init/04 на проде, а тесты грузят baseline на postgres:16-alpine, где pg_cron нет.
# Схемные эффекты миграций этого не требуют (функции ссылаются на 'cold' строкой при
# check_function_bodies=false; cron.schedule — данные, живут в init/04). Если будущая
# миграция затребует pg_cron/tablespace — добавить их setup сюда явно.

echo ">> загрузка текущего baseline"
[[ -s "$BASELINE" ]] && docker exec "${PGENV[@]}" "$CTR" psql -v ON_ERROR_STOP=1 -q -f /pg/init/02_baseline.sql

echo ">> накат непринятых миграций"
docker exec "${PGENV[@]}" -e MIGRATIONS_DIR=/pg/migrations "$CTR" bash /pg/migrate.sh

echo ">> снимок схемы → $BASELINE"
docker exec "${PGENV[@]}" "$CTR" pg_dump -U bindatacoll -d market_analytics \
    --schema-only --no-owner --no-privileges \
    | grep -vE '^\\(un)?restrict ' > "$BASELINE"

# Сид schema_migrations: --schema-only берёт только структуру таблицы, не строки.
{
    echo "$SENTINEL"
    docker exec "${PGENV[@]}" "$CTR" psql -tAq -c \
        'SELECT format('"'"'INSERT INTO public."schema_migrations"("Version","Checksum") VALUES (%L,%L) ON CONFLICT DO NOTHING;'"'"', "Version","Checksum") FROM public."schema_migrations" ORDER BY "Version";'
} >> "$BASELINE"

echo ">> свёрнутые активные миграции → archive/"
mkdir -p "$PG_DIR/migrations/archive"
shopt -s nullglob
for f in "$PG_DIR"/migrations/[0-9][0-9][0-9]_*.sql; do
    mv "$f" "$PG_DIR/migrations/archive/"
done

echo "Готово: $BASELINE перегенерирован. Проверь git diff и закоммить baseline + миграции."
