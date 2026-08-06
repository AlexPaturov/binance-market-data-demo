#!/usr/bin/env bash
# Installs Docker Engine and the Compose plugin on a clean Linux host, then starts demo.
set -euo pipefail

if [[ $(uname -s) != Linux ]]; then
    echo "Этот setup-скрипт предназначен для Linux." >&2
    exit 1
fi

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
TARGET_USER=${SUDO_USER:-$USER}

as_root() {
    if (( EUID == 0 )); then
        "$@"
    elif command -v sudo >/dev/null 2>&1; then
        sudo "$@"
    else
        echo "Нужны root-права или sudo для установки Docker." >&2
        exit 1
    fi
}

install_docker() {
    if [[ ! -r /etc/os-release ]]; then
        echo "Не удалось определить Linux-дистрибутив. Установите Docker Engine и Compose plugin вручную." >&2
        exit 1
    fi

    . /etc/os-release
    case "$ID" in
        ubuntu|debian)
            echo "Устанавливаю Docker Engine и Compose plugin для $PRETTY_NAME..."
            as_root apt-get update
            as_root apt-get install -y ca-certificates curl
            as_root install -m 0755 -d /etc/apt/keyrings
            as_root curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
            as_root chmod a+r /etc/apt/keyrings/docker.asc
            echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID $VERSION_CODENAME stable" | as_root tee /etc/apt/sources.list.d/docker.list >/dev/null
            as_root apt-get update
            as_root apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
            ;;
        fedora)
            echo "Устанавливаю Docker Engine и Compose plugin для Fedora..."
            as_root dnf -y install dnf-plugins-core
            as_root dnf config-manager --add-repo https://download.docker.com/linux/fedora/docker-ce.repo
            as_root dnf -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
            ;;
        arch)
            echo "Устанавливаю Docker Engine и Compose plugin для Arch Linux..."
            as_root pacman -Sy --noconfirm docker docker-compose
            ;;
        *)
            echo "Неподдерживаемый дистрибутив: $PRETTY_NAME" >&2
            echo "Установите Docker Engine и Compose plugin вручную: https://docs.docker.com/engine/install/" >&2
            exit 1
            ;;
    esac
}

echo "== Linux demo setup =="

if ! command -v docker >/dev/null 2>&1; then
    install_docker
else
    echo "Docker CLI уже установлен."
fi

if command -v systemctl >/dev/null 2>&1; then
    as_root systemctl enable --now docker
fi

if [[ $EUID -ne 0 ]] && ! id -nG "$TARGET_USER" | tr " " "\n" | grep -qx docker; then
    as_root usermod -aG docker "$TARGET_USER"
    echo "Пользователь $TARGET_USER добавлен в группу docker. Продолжаю в новой группе..."
    if command -v sg >/dev/null 2>&1; then
        exec sg docker -c "exec \"$SCRIPT_DIR/demo-setup.sh\""
    fi
    echo "Не найдена утилита sg. Перелогиньтесь и повторите: $SCRIPT_DIR/demo-setup.sh" >&2
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    echo "Docker daemon недоступен. Проверьте: sudo systemctl status docker" >&2
    exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
    echo "Docker Compose plugin недоступен после установки." >&2
    exit 1
fi

echo "Docker Engine и Compose готовы."
exec "$SCRIPT_DIR/demo-start.sh"
