# Запуск demo

Самодостаточный стек с предзагруженной БД (BTCUSDT за февраль 2026): без сбора из Binance и без Azure B2C. От чистой машины до открытого `http://localhost:7002`. Образы мультиплатформенные (amd64 / arm64, включая Apple Silicon).

Ниже — блок под свою ОС (предусловия + запуск), затем общие для всех разделы «Вход» и «Остановка».

## Linux

На чистой Linux-машине скрипт установит Docker Engine и Compose plugin, затем запустит demo. Поддерживаются Ubuntu/Debian, Fedora и Arch Linux. Нужны root-права или `sudo`.

```bash
./docker/demo-setup.sh
```

## macOS

Предусловия: **Docker Desktop for Mac** установлен и запущен (в меню-баре кит «Docker Desktop is running»). Установка при необходимости — `brew install --cask docker`, затем запусти Docker Desktop из Launchpad. Работает на Apple Silicon и Intel.

```bash
./docker/demo-start.sh
```

Если папку копировали (не `git clone`), у скрипта мог потеряться флаг исполнения (`permission denied`) — запусти через `bash ./docker/demo-start.sh` или верни флаг: `chmod +x docker/demo-start.sh docker/demo-stop.sh`. `sudo` не нужен.

## Windows

Все команды — в **PowerShell**. Предусловия: Windows 10 (2004+) или 11 64-bit; права администратора для установки; виртуализация (VT-x / AMD-V) включена в BIOS/UEFI.

**Чистая машина без Docker** — один скрипт ставит WSL2 + Docker Desktop и запускает demo (PowerShell от администратора). Про перезагрузку скрипт скажет сам; после неё запусти ту же команду снова, он продолжит (перезагрузок бывает 1–2):

```powershell
powershell -ExecutionPolicy Bypass -File .\docker\demo-setup.ps1
```

**Docker Desktop уже установлен:**

```powershell
powershell -ExecutionPolicy Bypass -File .\docker\demo-start.ps1
```

---

## Вход

Скрипт создаёт `.env` из `.env.example`, собирает образы (первый раз — несколько минут), поднимает стек, дожидается готовности и открывает браузер. На странице входа выбери роль: **Viewer** (просмотр), **Operator** (+ операции), **Admin** (+ Hangfire). В БД уже есть срез BTCUSDT за февраль 2026 — сразу видны график, панель Months и Data Quality.

## Остановка

На Linux/macOS — `demo-stop.sh`, на Windows — `demo-stop.ps1` (флаги `-Volumes` / `-Purge` вместо `-v` / `--purge`):

```bash
./docker/demo-stop.sh            # остановить, данные сохранить
./docker/demo-stop.sh -v         # + удалить тома (seed перезагрузится при старте)
./docker/demo-stop.sh --purge    # + удалить тома и собранные demo-образы
```

## Если что-то пошло не так

1. **Браузер открывает `https://localhost:7002` и ошибку** (все ОС) — HSTS-кеш от прежних запусков. Зайди по `http://localhost:7002` в приватном окне или очисти HSTS (Chrome: `chrome://net-internals/#hsts` → Delete `localhost`; Safari — очисти данные сайта для localhost).
2. **Порт 7002 занят** — освободи порт или останови конфликтующий процесс (`ss -ltnp | grep 7002` на Linux, `lsof -i :7002` на macOS, `Test-NetConnection localhost -Port 7002` на Windows).
3. **macOS — `Cannot connect to the Docker daemon`** — Docker Desktop не запущен. Запусти из Launchpad, дождись «Docker Desktop is running». При падении сборки по памяти — Settings → Resources, лимит памяти от 4 ГБ.
4. **Windows — `running scripts is disabled`** — запускай через `powershell -ExecutionPolicy Bypass -File <скрипт>`.
5. **Windows — setup ждёт движок Docker** — открой Docker Desktop вручную, прими условия первого запуска, дождись **Engine running**, запусти скрипт снова. Если движок не стартует — проверь виртуализацию: `(Get-CimInstance Win32_ComputerSystem).HypervisorPresent` должно быть `True`.

Demo поддерживается на Linux, macOS и Windows.
