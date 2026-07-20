#!/usr/bin/env bash
#
# Накат непринятых миграций по журналу public."schema_migrations".
# Идемпотентно: применяет только версии, которых нет в целевой БД.
#
# Соединение — стандартными libpq-переменными (PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE).
# Требует psql и sha256sum в PATH. От Docker не зависит: годится и внутри контейнера
# (prod: docker exec bdc_db), и на CI (postgresql-client + service-контейнер).
#
# Источник миграций: migrations/ и migrations/archive/ (по возрастанию номера в имени).
# Переменные:
#   MIGRATIONS_DIR              — каталог с миграциями (по умолчанию рядом со скриптом)
#   MIGRATE_LOCK_TIMEOUT        — lock_timeout (по умолчанию 5s): не взял лок быстро → аборт
#   MIGRATE_STATEMENT_TIMEOUT   — statement_timeout (по умолчанию 30min)
# Заголовок файла с меткой "@migrate-no-timeout" снимает statement_timeout
# (для заведомо долгих операций вроде CREATE INDEX CONCURRENTLY).
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIG_DIR="${MIGRATIONS_DIR:-$SCRIPT_DIR/migrations}"
LOCK_TIMEOUT="${MIGRATE_LOCK_TIMEOUT:-5s}"
STMT_TIMEOUT="${MIGRATE_STATEMENT_TIMEOUT:-30min}"

# Журнал применённых миграций. Живёт в самой БД — один источник правды про то,
# что уже накатано, отдельно от baseline-снимка.
psql -v ON_ERROR_STOP=1 -q <<'SQL'
CREATE TABLE IF NOT EXISTS public."schema_migrations" (
    "Version"   text PRIMARY KEY,
    "AppliedAt" timestamptz NOT NULL DEFAULT now(),
    "Checksum"  text NOT NULL
);
SQL

# Все миграции: активные + архив, по возрастанию номера (по basename, не по пути).
mapfile -t files < <(find "$MIG_DIR" -maxdepth 2 -name '[0-9][0-9][0-9]_*.sql' -printf '%f\t%p\n' \
                        | sort | cut -f2-)

applied=0
for path in "${files[@]}"; do
    version="$(basename "$path" .sql)"
    checksum="$(sha256sum "$path" | cut -d' ' -f1)"
    prev="$(psql -tAq -c "SELECT \"Checksum\" FROM public.\"schema_migrations\" WHERE \"Version\" = '$version'")"

    if [[ -n "$prev" ]]; then
        [[ "$prev" != "$checksum" ]] && \
            echo "WARN: $version уже применена, но контрольная сумма файла изменилась" >&2
        continue
    fi

    echo ">> накатываю $version"
    if head -n 5 "$path" | grep -qiE '@migrate-no-timeout'; then
        psql -v ON_ERROR_STOP=1 -q -f "$path"
    else
        PGOPTIONS="-c lock_timeout=$LOCK_TIMEOUT -c statement_timeout=$STMT_TIMEOUT" \
            psql -v ON_ERROR_STOP=1 -q -f "$path"
    fi
    psql -v ON_ERROR_STOP=1 -q \
        -c "INSERT INTO public.\"schema_migrations\"(\"Version\",\"Checksum\") VALUES ('$version','$checksum')"
    applied=$((applied + 1))
done

echo "Готово. Накачено новых миграций: $applied"
