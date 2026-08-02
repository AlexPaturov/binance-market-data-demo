#!/usr/bin/env bash
# Останавливает demo-стек.
#   ./docker/demo-stop.sh            # тома сохраняются
#   ./docker/demo-stop.sh -v         # + удалить тома (seed перезагрузится при старте)
#   ./docker/demo-stop.sh --purge    # + удалить тома И локально собранные demo-образы
set -euo pipefail

cd "$(dirname "$0")/compose"

# Compose бывает как плагин v2 (`docker compose`) и как отдельный бинарь v1 (`docker-compose`).
if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE=(docker-compose)
else
    echo "Docker Compose не найден. Нужен Docker Desktop или docker-compose." >&2
    exit 1
fi

EXTRA=()
PURGE=0
case "${1:-}" in
    -v|--volumes)
        EXTRA=(--volumes)
        echo "Удаляю тома — seed будет пересоздан при следующем старте."
        ;;
    --purge)
        EXTRA=(--volumes)
        PURGE=1
        echo "Удаляю тома и собранные demo-образы."
        ;;
esac

"${COMPOSE[@]}" -f docker-compose.demo.yml down --timeout 30 "${EXTRA[@]}"

if [ "${PURGE}" -eq 1 ]; then
    docker image rm -f bdc/datamanager:demo bdc/worker:demo bdc/postgres-cron:16 2>/dev/null || true
fi
