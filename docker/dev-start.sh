#!/usr/bin/env bash
set -e

# Mount database disk if not mounted
if ! mountpoint -q /mnt/ext; then
    echo "Mounting /mnt/ext..."
    sudo mount /mnt/ext
fi

# Ensure data directories exist
mkdir -p "$HOME/bdc_data/Trades/Downloaded"
mkdir -p "$HOME/bdc_data/Trades/Unpacked"
mkdir -p "$HOME/bdc_data/Ohlcv/Downloaded"
mkdir -p "$HOME/bdc_data/Ohlcv/Unpacked"

# Start infrastructure
cd "$(dirname "$0")/compose"
docker compose \
  -f docker-compose.yml \
  -f docker-compose.db.yml \
  -f docker-compose.rabbit.yml \
  -f docker-compose.seq.yml \
  up -d
