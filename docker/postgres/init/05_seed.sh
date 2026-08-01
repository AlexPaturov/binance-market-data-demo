#!/bin/bash
# Загрузка demo-среза (BTCUSDT, февраль 2026) на чистом томе — после схемы (02),
# Hangfire (03) и cold/pg_cron (04). Работает только когда смонтированы seed-данные:
# demo-compose задаёт SEED_DATA_DIR и монтирует туда *.csv.gz. В dev/prod переменной
# нет — скрипт тихо выходит, обычная инициализация его не замечает.
set -euo pipefail

: "${SEED_DATA_DIR:=}"
if [ -z "${SEED_DATA_DIR}" ] || [ ! -f "${SEED_DATA_DIR}/01_tracked_symbols.csv.gz" ]; then
    echo "[seed] SEED_DATA_DIR не задан или данные не смонтированы — пропускаю (не demo)."
    exit 0
fi

DB="${POSTGRES_DB:-market_analytics}"
PSQL=(psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER}" -d "${DB}")

# Февраль 2026 в мс (UTC): [2026-02-01, 2026-03-01). На пустой БД floor ретенции = 0,
# поэтому партиции месяца создаются штатной процедурой независимо от текущей даты.
echo "[seed] создаю партиции февраля 2026..."
"${PSQL[@]}" -c "SELECT public.sp_ensure_month_partitions(1769904000000);"

load() { # $1=таблица $2=файл
    echo "[seed] $1 <- $2"
    gunzip -c "${SEED_DATA_DIR}/$2" \
        | "${PSQL[@]}" -c "COPY public.\"$1\" FROM STDIN WITH (FORMAT csv, HEADER true)"
}

load "TrackedSymbols"   "01_tracked_symbols.csv.gz"
load "ArchiveImportLog" "02_archive_import_log.csv.gz"
load "MonthSeal"        "03_month_seal.csv.gz"
load "Ohlcv_1min"       "04_ohlcv_1min.csv.gz"
load "Ohlcv_Features"   "05_ohlcv_features.csv.gz"
load "Trades"           "06_trades_sample.csv.gz"

echo "[seed] готово."
