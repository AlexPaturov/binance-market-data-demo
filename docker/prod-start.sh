#!/usr/bin/env bash
# Запуск прод-стека. Отказывается стартовать, если внешний диск с данными
# Postgres не примонтирован — иначе Postgres создаст пустую БД на системном диске.
set -euo pipefail

COMPOSE_DIR="/opt/BinanceCollector/docker/compose"
COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml"
DISK_UUID="25ddc534-13e6-479e-8392-a4487a975c80"
MOUNT_POINT="/mnt/ext"
PGDATA="${MOUNT_POINT}/postgres_data"
DB_TIMEOUT=120   # сек на то, чтобы bdc_db стал healthy
WORKER_UID=1654  # USER app в docker/Dockerfile

RED=$'\e[31m'; GREEN=$'\e[32m'; YELLOW=$'\e[33m'; RESET=$'\e[0m'
ok()   { echo "${GREEN}[OK]${RESET}   $*"; }
warn() { echo "${YELLOW}[WARN]${RESET} $*"; }
die()  { echo "${RED}[FAIL]${RESET} $*" >&2; exit 1; }

# --- 1. Диск -----------------------------------------------------------------
if ! mountpoint -q "$MOUNT_POINT"; then
    warn "$MOUNT_POINT не примонтирован, монтирую..."
    sudo mount "$MOUNT_POINT" || die "не смог примонтировать $MOUNT_POINT. Диск подключён физически? lsblk -f | grep $DISK_UUID"
fi
mountpoint -q "$MOUNT_POINT" || die "$MOUNT_POINT так и не примонтирован — стартовать нельзя"
ok "$MOUNT_POINT примонтирован ($(df -h --output=size "$MOUNT_POINT" | tail -1 | tr -d ' '))"

# --- 2. Данные Postgres на месте ---------------------------------------------
# Защита от главной аварии: пустой каталог => Postgres молча создаст новую БД.
# Каталог данных — drwx------ uid 70, обычным пользователем внутрь не заглянуть, отсюда sudo.
sudo test -f "${PGDATA}/PG_VERSION" || die "в ${PGDATA} нет PG_VERSION — это не каталог данных Postgres. Стартовать нельзя, иначе будет создана пустая БД."
ok "данные Postgres на месте ($(sudo du -sh "$PGDATA" 2>/dev/null | cut -f1))"

# --- 3. Старт ----------------------------------------------------------------
cd "$COMPOSE_DIR" || die "нет каталога $COMPOSE_DIR"
[[ -f .env ]] || die "нет .env в $COMPOSE_DIR — контейнеры не поднимутся"

echo "Поднимаю стек..."
$COMPOSE up -d || die "docker compose up завершился с ошибкой"

# --- 4. Ждём БД --------------------------------------------------------------
echo -n "Жду bdc_db (healthy), до ${DB_TIMEOUT}с"
for ((i = 0; i < DB_TIMEOUT; i++)); do
    status=$(docker inspect --format '{{.State.Health.Status}}' bdc_db 2>/dev/null || echo "missing")
    [[ "$status" == "healthy" ]] && break
    echo -n "."
    sleep 1
done
echo
[[ "${status:-}" == "healthy" ]] || die "bdc_db не стал healthy за ${DB_TIMEOUT}с (статус: ${status:-нет контейнера}). Логи: docker logs bdc_db --tail 50"
ok "bdc_db healthy"

# --- 5. Схема на месте (а не пустая БД) --------------------------------------
partitions=$(docker exec bdc_db psql -U "${POSTGRES_USER:-bindatacoll}" -d market_analytics -tAc \
    "SELECT count(*) FROM pg_inherits WHERE inhparent = 'public.\"Trades\"'::regclass;" 2>/dev/null || echo "0")
[[ "$partitions" -gt 0 ]] || die "в market_analytics нет партиций Trades — похоже, поднялась ПУСТАЯ база. Немедленно остановить: ./prod-stop.sh"
ok "схема на месте (партиций Trades: ${partitions})"

# --- 6. Права на volume с архивами -------------------------------------------
if ! docker exec bdc_worker sh -c 'touch /opt/bdc_data/Trades/Unpacked/.wtest 2>/dev/null && rm /opt/bdc_data/Trades/Unpacked/.wtest' 2>/dev/null; then
    warn "bdc_worker не может писать в /opt/bdc_data/Trades/Unpacked."
    warn "Починить: docker run --rm -v binancecollector_bdc_data:/data alpine chown -R ${WORKER_UID}:${WORKER_UID} /data"
else
    ok "bdc_worker пишет в bdc_data"
fi

# --- 7. Итог -----------------------------------------------------------------
echo
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps --format "table {{.Name}}\t{{.Status}}"
echo
ok "Прод поднят. Hangfire: https://hangfire.jahasim.com  Логи: https://seq.jahasim.com"
