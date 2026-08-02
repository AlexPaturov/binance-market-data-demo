#!/usr/bin/env bash
# Поднимает demo-стек и открывает браузер на http://localhost:7002.
# Автооткрытие делается здесь, на хосте: контейнер DataManager доступа к дисплею не имеет.
set -euo pipefail

cd "$(dirname "$0")/compose"

if [ ! -f .env ]; then
    cp .env.example .env
    echo "Создан .env из .env.example."
fi

docker compose -f docker-compose.demo.yml up -d --build

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
