#!/usr/bin/env bash
# Поднимает demo-стек и открывает браузер на http://localhost:7002.
# Автооткрытие делается здесь, на хосте: контейнер DataManager доступа к дисплею не имеет.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
cd "$SCRIPT_DIR/compose"

if ! command -v docker >/dev/null 2>&1; then
    echo "Docker Engine не найден." >&2
    echo "На чистом Linux запустите: $SCRIPT_DIR/demo-setup.sh" >&2
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    echo "Docker daemon недоступен. Запустите Docker или проверьте права пользователя." >&2
    exit 1
fi

# Compose бывает как плагин v2 (`docker compose`) и как отдельный бинарь v1 (`docker-compose`).
if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE=(docker-compose)
else
    echo "Docker Compose не найден." >&2
    echo "На чистом Linux запустите: $SCRIPT_DIR/demo-setup.sh" >&2
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
