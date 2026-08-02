#!/usr/bin/env bash
# Останавливает demo-стек. С флагом -v (или --volumes) удаляет и тома —
# при следующем старте БД переинициализируется и seed загрузится заново.
set -euo pipefail

cd "$(dirname "$0")/compose"

EXTRA=()
case "${1:-}" in
    -v|--volumes)
        EXTRA=(--volumes)
        echo "Удаляю тома — seed будет пересоздан при следующем старте."
        ;;
esac

docker compose -f docker-compose.demo.yml down --timeout 30 "${EXTRA[@]}"
