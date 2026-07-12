#!/usr/bin/env bash
# Штатная остановка прод-стека: сначала приложения, потом БД с запасом на flush,
# проверка чистого шатдауна Postgres, отмонтирование диска и парковка головок.
#
# Использование:
#   ./prod-stop.sh              — остановить стек, отмонтировать диск
#   ./prod-stop.sh --poweroff   — то же + выключить сервер
set -euo pipefail

COMPOSE_DIR="/opt/BinanceCollector/docker/compose"
COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml"
DISK_UUID="25ddc534-13e6-479e-8392-a4487a975c80"
MOUNT_POINT="/mnt/ext"
STOP_TIMEOUT=120  # сек Postgres на flush; дефолтных 10 может не хватить

POWEROFF=false
[[ "${1:-}" == "--poweroff" ]] && POWEROFF=true

RED=$'\e[31m'; GREEN=$'\e[32m'; YELLOW=$'\e[33m'; RESET=$'\e[0m'
ok()   { echo "${GREEN}[OK]${RESET}   $*"; }
warn() { echo "${YELLOW}[WARN]${RESET} $*"; }
die()  { echo "${RED}[FAIL]${RESET} $*" >&2; exit 1; }

cd "$COMPOSE_DIR" || die "нет каталога $COMPOSE_DIR"

# --- 1. Сначала приложения — прекращаем запись в БД ---------------------------
echo "Останавливаю приложения..."
$COMPOSE stop bdc_worker bdc_datamanager || die "не смог остановить приложения"
ok "bdc_worker и bdc_datamanager остановлены"
# Незавершённые джобы импорта безопасны: ZIP/CSV удаляются только после успешной
# вставки, вставка идемпотентна (ON CONFLICT DO NOTHING). Очередь доработает при старте.

# --- 2. Остальное, с запасом по времени --------------------------------------
echo "Останавливаю остальной стек (таймаут ${STOP_TIMEOUT}с)..."
$COMPOSE stop --timeout "$STOP_TIMEOUT" || die "docker compose stop завершился с ошибкой"
ok "контейнеры остановлены"

# --- 3. Postgres закрылся чисто? ---------------------------------------------
if docker logs bdc_db --tail 20 2>&1 | grep -q "database system is shut down"; then
    ok "Postgres закрылся чисто (database system is shut down)"
else
    die "в логах bdc_db НЕТ 'database system is shut down'. Диск НЕ отмонтирован. Разберитесь: docker logs bdc_db --tail 50"
fi

# --- 4. Отмонтировать диск ---------------------------------------------------
if mountpoint -q "$MOUNT_POINT"; then
    echo "Отмонтирую $MOUNT_POINT..."
    if ! sudo umount "$MOUNT_POINT"; then
        warn "umount не прошёл (target is busy?). Кто держит:"
        sudo lsof +D "$MOUNT_POINT" 2>/dev/null | head || true
        die "не смог отмонтировать $MOUNT_POINT"
    fi
    ok "$MOUNT_POINT отмонтирован"

    # Парковка головок — диск внешний, лучше не дёргать питание на лету
    DISK_DEV=$(lsblk -o UUID,PKNAME -rn | awk -v uuid="$DISK_UUID" '$1 == uuid { print "/dev/" $2 }')
    if [[ -n "$DISK_DEV" ]]; then
        sudo hdparm -Y "$DISK_DEV" >/dev/null 2>&1 && ok "головки диска $DISK_DEV запаркованы" \
            || warn "не смог запарковать $DISK_DEV (не критично)"
    fi
else
    warn "$MOUNT_POINT и так не был примонтирован"
fi

# --- 5. Выключение -----------------------------------------------------------
echo
if $POWEROFF; then
    ok "Всё остановлено. Выключаю сервер..."
    sudo poweroff
else
    ok "Всё остановлено. Диск можно отключать. Для выключения: sudo poweroff"
fi
