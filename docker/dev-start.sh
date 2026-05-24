#!/usr/bin/env bash
set -e

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
