#!/usr/bin/env bash
#
# CI-страж дрейфа схемы: baseline обязан УЖЕ включать эффект всех активных миграций.
#
#   грузим init/02_baseline.sql → dump BEFORE
#   накатываем migrations/ (migrate.sh; archive уже в сиде → пропускается)
#   dump AFTER
#   BEFORE == AFTER ?  → зелено (миграций нет либо они свёрнуты)
#   иначе              → красно: миграция уехала вперёд baseline
#
# Нужен только Docker. Образ — REGEN_IMAGE (по умолчанию тот же, что у regen-schema),
# чтобы pg_dump-версия совпадала и before/after были сравнимы.
#
set -euo pipefail

PG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE="${REGEN_IMAGE:-bdc/postgres-cron:16}"
CTR="bdc_drift_$$"
PW="drift_$$"

cleanup() { docker rm -f "$CTR" >/dev/null 2>&1 || true; }
trap cleanup EXIT

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

dump() {
    docker exec "${PGENV[@]}" "$CTR" pg_dump -U bindatacoll -d market_analytics \
        --schema-only --no-owner --no-privileges | grep -vE '^\\(un)?restrict '
}

docker exec "${PGENV[@]}" "$CTR" psql -v ON_ERROR_STOP=1 -q -f /pg/init/02_baseline.sql
before="$(dump)"
docker exec "${PGENV[@]}" -e MIGRATIONS_DIR=/pg/migrations "$CTR" bash /pg/migrate.sh
after="$(dump)"

if [[ "$before" == "$after" ]]; then
    echo "✓ baseline включает все активные миграции (no-op). Дрейфа нет."
    exit 0
fi

echo "✗ ДРЕЙФ СХЕМЫ: активная миграция не свёрнута в baseline."
echo "  Прогони docker/postgres/regen-schema.sh и закоммить обновлённый init/02_baseline.sql."
echo "--- diff: baseline → baseline + миграции ---"
diff <(printf '%s\n' "$before") <(printf '%s\n' "$after") || true
exit 1
