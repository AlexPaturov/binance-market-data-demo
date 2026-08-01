#!/bin/bash
# Пересоздаёт demo-срез (BTCUSDT, февраль 2026) из БД-источника в *.csv.gz рядом с этим скриптом.
# Срез: свечи/фичи/журнал покрытия/печать месяца за весь февраль (закрытый месяц целиком),
# плюс 10-минутный сэмпл сырых сделок (полный месяц Trades — гигабайты, в репозиторий не кладём).
#
# Источник задаётся переменной SRC — командой, которая принимает SQL первым аргументом «-c»
# и пишет результат на stdout. По умолчанию — локальный dev-контейнер. Пример для прода:
#   SRC='ssh -p 2237 prod docker exec -i bdc_db psql -U bindatacoll -d market_analytics' ./gen-seed.sh
set -euo pipefail

SEED_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${SRC:-docker exec bdc_db psql -U bindatacoll -d market_analytics}"

# Границы февраля 2026 в мс (UTC) и 10-минутное окно сэмпла сделок.
FEB_FROM=1769904000000   # 2026-02-01T00:00:00Z
FEB_TO=1772323200000     # 2026-03-01T00:00:00Z
SMP_FROM=1770076800000   # 2026-02-03T00:00:00Z
SMP_TO=1770077400000     # 2026-02-03T00:10:00Z

dump() { # $1=файл  $2=SELECT
    echo "[gen] $1"
    $SRC -qAt -c "SET statement_timeout='90s'; COPY ($2) TO STDOUT WITH (FORMAT csv, HEADER true)" \
        | gzip > "${SEED_DIR}/$1"
}

dump "01_tracked_symbols.csv.gz" \
    "SELECT * FROM public.\"TrackedSymbols\" WHERE \"Symbol\"='BTCUSDT'"
dump "02_archive_import_log.csv.gz" \
    "SELECT * FROM public.\"ArchiveImportLog\" WHERE \"Symbol\"='BTCUSDT' AND \"TradeDate\">=date '2026-02-01' AND \"TradeDate\"<date '2026-03-01'"
dump "03_month_seal.csv.gz" \
    "SELECT * FROM public.\"MonthSeal\" WHERE \"PeriodMonth\"=date '2026-02-01'"
dump "04_ohlcv_1min.csv.gz" \
    "SELECT * FROM public.\"Ohlcv_1min\" WHERE \"Symbol\"='BTCUSDT' AND \"OpenTime\">=${FEB_FROM} AND \"OpenTime\"<${FEB_TO}"
dump "05_ohlcv_features.csv.gz" \
    "SELECT * FROM public.\"Ohlcv_Features\" WHERE \"Symbol\"='BTCUSDT' AND \"OpenTime\">=${FEB_FROM} AND \"OpenTime\"<${FEB_TO}"
dump "06_trades_sample.csv.gz" \
    "SELECT * FROM public.\"Trades\" WHERE \"Symbol\"='BTCUSDT' AND \"TradeTime\">=${SMP_FROM} AND \"TradeTime\"<${SMP_TO}"

echo "[gen] готово. Размер:"
du -ch "${SEED_DIR}"/*.csv.gz | tail -1
