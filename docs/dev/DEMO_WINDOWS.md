# Запуск demo на Windows

Пошагово: от чистой машины до открытого `http://localhost:7002`. Все команды — в **PowerShell**.

## Предусловия

1. Windows 10 версии 2004+ или Windows 11, 64-bit.
2. Права **администратора** (для установки WSL2 и Docker Desktop).
3. Виртуализация (VT-x / AMD-V) включена в BIOS/UEFI.
4. Проект на машине — через `git clone` либо скопированной папкой.

## Вариант A. Чистая машина без Docker

Один скрипт ставит всё и запускает demo. PowerShell **от администратора** (Пуск → PowerShell → «Запуск от имени администратора»):

```powershell
cd <папка-проекта>
powershell -ExecutionPolicy Bypass -File .\docker\demo-setup.ps1
```

Скрипт: проверит виртуализацию → поставит WSL2 → поставит Docker Desktop → запустит движок → поднимет demo и откроет браузер.

Если скрипт сообщил про перезагрузку — перезагрузи ПК и запусти ту же команду снова (он продолжит с места, где остановился). Перезагрузок может быть 1–2.

## Вариант B. Docker Desktop уже установлен

```powershell
cd <папка-проекта>
powershell -ExecutionPolicy Bypass -File .\docker\demo-start.ps1
```

Скрипт создаст `.env`, соберёт образы (первый раз — несколько минут), поднимет стек и откроет `http://localhost:7002`.

## Вход

На странице входа выбери роль: **Viewer** (просмотр), **Operator** (+ операции), **Admin** (+ Hangfire). В БД предзагружен срез BTCUSDT за февраль 2026 — сразу видны график, панель Months и Data Quality.

## Остановка

```powershell
.\docker\demo-stop.ps1            # остановить, данные сохранить
.\docker\demo-stop.ps1 -Volumes   # + удалить тома (seed перезагрузится при старте)
.\docker\demo-stop.ps1 -Purge     # + удалить тома и собранные demo-образы (освободить место)
```

## Если что-то пошло не так

1. **`... cannot be loaded because running scripts is disabled`** — политика PowerShell. Запускай через `powershell -ExecutionPolicy Bypass -File <скрипт>` (как в примерах выше).
2. **Скрипт setup ждёт движок Docker и не продолжает** — открой Docker Desktop вручную, прими условия (первый запуск требует согласия в окне), дождись статуса **Engine running**, запусти скрипт снова.
3. **Docker Desktop не стартует / ошибка виртуализации** — включи VT-x / AMD-V в BIOS/UEFI; проверь: `(Get-CimInstance Win32_ComputerSystem).HypervisorPresent` должно быть `True`.
4. **Браузер открывает `https://localhost:7002` и ошибку** — это HSTS-кеш браузера от прежних запусков. В адресной строке зайди по `http://localhost:7002` в новом приватном окне, либо очисти HSTS (Chrome: `chrome://net-internals/#hsts` → Delete `localhost`).
5. **Порт 7002 занят** — проверь `Test-NetConnection localhost -Port 7002`; освободи порт или останови конфликтующий процесс.
