# Документация по настройке сети и безопасности сервера (analserver)

**Дата:** 20.12.2025  
**ОС:** Ubuntu 24.04 (ThinkPad)  
**Роль:** Docker Host (RabbitMQ, Postgres, .NET Workers, Cloudflare Tunnel)

---

## 1. Настройка Firewall (UFW)

Мы перешли от политики «разрешено всё» к политике «разрешено только доверенное». Это решает проблемы с безопасностью и connectivity между контейнерами.

**Политика по умолчанию:**
*   **Incoming:** DENY (Запрещено)
*   **Outgoing:** ALLOW (Разрешено)

### Активные правила

| Сервис / Назначение | Порт / IP | Протокол | Комментарий | Описание |
| :--- | :--- | :--- | :--- | :--- |
| **SSH** | `2237` | TCP | `SSH Custom Port` | Доступ к терминалу сервера. Стандартный порт 22 закрыт. |
| **Local LAN** | `192.168.0.0/24` | Any | `Home Local Network` | Полный доступ с домашних устройств (Wi-Fi/LAN), включая Cockpit (9090) и другие админки. |
| **Docker Internal** | `172.16.0.0/12` | Any | `Docker Internal Traffic` | **Критично.** Разрешает контейнерам общаться друг с другом через виртуальные мосты. Без этого RabbitMQ недоступен для воркеров (`Connection refused`). |
| **Tailscale P2P** | `41641` | UDP | `Tailscale Direct` | Для прямого соединения (P2P) между узлами VPN, минуя DERP-реле. |
| **Tailscale Network** | `100.64.0.0/10` | Any | `Tailscale Network` | Полный доступ к сервисам через VPN интерфейс `tailscale0`. |
| **Cloudflare** | *См. список IP* | Any | `CF IP Range ...` | Разрешает входящий трафик от серверов Cloudflare. Критично для работы сайта при смене IP (Wi-Fi <-> Кабель). |

### Список IP-диапазонов Cloudflare (Allow List)
Эти подсети добавлены в Allow, чтобы туннель не блокировался фаерволом:
*   `198.41.128.0/17`
*   `173.245.48.0/20`
*   `103.21.244.0/22`
*   `103.22.200.0/22`
*   `103.31.4.0/22`
*   `141.101.64.0/18`
*   `108.162.192.0/18`
*   `190.93.240.0/20`
*   `188.114.96.0/20`
*   `197.234.240.0/22`
*   `162.158.0.0/15`
*   `104.16.0.0/12`
*   `172.64.0.0/13`
*   `131.0.72.0/22`

### Полезные команды
```bash
# Просмотр правил с номерами
sudo ufw status numbered

# Перезагрузка правил
sudo ufw reload

# Смотрим в реальном времени логи
sudo journalctl -f
```
## 2. Автоматизация смены сети (NetworkManager Dispatcher)
Проблема: При переключении с Wi-Fi на Кабель меняется интерфейс и IP шлюза. Контейнер cloudflared теряет связь и висит (ошибка 1033), пока его не перезапустят.

Решение: Скрипт, который автоматически перезапускает контейнер туннеля при поднятии любого физического интерфейса.

Файл: /etc/NetworkManager/dispatcher.d/99-restart-cloudflared

Права: 755 (chmod +x)
```bash

# Даём права
sudo chmod +x /etc/NetworkManager/dispatcher.d/99-restart-cloudflared

# Проверка прав
sudo chown root:root /etc/NetworkManager/dispatcher.d/99-restart-cloudflared
```
Содержимое скрипта:
```bash
#!/bin/bash

INTERFACE=$1
STATUS=$2

# Логируем вызов
logger "NM-Dispatcher triggered: $INTERFACE is $STATUS"

# Игнорируем виртуальные интерфейсы Docker и локальную петлю, 
# чтобы избежать бесконечного цикла рестартов.
if [[ "$INTERFACE" == *"docker"* ]] || [[ "$INTERFACE" == *"br-"* ]] || [[ "$INTERFACE" == *"veth"* ]] || [[ "$INTERFACE" == "lo" ]]; then
    exit 0
fi

# Если физическая сеть поднялась (up или vpn-up)
if [ "$STATUS" = "up" ] || [ "$STATUS" = "vpn-up" ]; then
    logger "Network is UP ($INTERFACE). Restarting Cloudflared..."
    
    # Жесткий перезапуск контейнера
    /usr/bin/docker restart cloudflared || logger "Failed to restart cloudflared"
fi
```