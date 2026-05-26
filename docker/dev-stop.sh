#!/usr/bin/env bash
set -e

cd "$(dirname "$0")/compose"
docker compose \
  -f docker-compose.yml \
  -f docker-compose.db.yml \
  -f docker-compose.rabbit.yml \
  -f docker-compose.seq.yml \
  down --timeout 30

# Unmount database disk and park HDD heads
if mountpoint -q /mnt/ext; then
    echo "Unmounting /mnt/ext..."
    sudo umount /mnt/ext
    sudo hdparm -Y /dev/sda
fi
