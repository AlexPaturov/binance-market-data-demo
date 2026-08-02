#!/usr/bin/env bash
# Поднимает demo-стек и открывает браузер на http://localhost:7002.
# Автооткрытие делается здесь, на хосте: контейнер DataManager доступа к дисплею не имеет.
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

if [ ! -f .env ]; then
    cp .env.example .env
    echo "Создан .env из .env.example."
fi

"${COMPOSE[@]}" -f docker-compose.demo.yml up -d --build

URL="http://localhost:7002"
echo "Жду готовности DataManager на ${URL} ..."
for _ in $(seq 1 90); do
    if curl -sf "${URL}/health/ready" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

case "$(uname -s)" in
    Linux*)                 xdg-open  "${URL}" >/dev/null 2>&1 || true ;;
    Darwin*)                open      "${URL}"               || true ;;
    MINGW*|MSYS*|CYGWIN*)   start ""  "${URL}"               || true ;;
esac

echo "Demo готово: ${URL} — на странице входа выберите роль."
