#!/usr/bin/env bash
set -e

cd "$(dirname "$0")/compose"
docker compose \
  -f docker-compose.yml \
  -f docker-compose.db.yml \
  -f docker-compose.rabbit.yml \
  -f docker-compose.seq.yml \
  down --timeout 30
