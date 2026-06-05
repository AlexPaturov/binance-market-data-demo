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
    DISK_UUID="25ddc534-13e6-479e-8392-a4487a975c80"
    DISK_DEV=$(lsblk -o UUID,PKNAME -rn | awk -v uuid="$DISK_UUID" '$1==uuid {print "/dev/" $2}')
    echo "Unmounting /mnt/ext ($DISK_DEV)..."
    sudo umount /mnt/ext
    sudo hdparm -Y "$DISK_DEV"
fi
