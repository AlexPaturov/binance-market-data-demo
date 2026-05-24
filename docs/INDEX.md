# Документация BinanceDataCollector

Навигация по документации проекта.

## Структура
docs/
├── INDEX.md                    ← этот файл
├── Server_Network_Config.md    ← UFW, Cloudflare IP, NetworkManager (прод-сервер)
├── TODO_POST_INITIAL_LOAD.md   ← action plan после завершения initial load (6 шагов)
├── INITIAL_DATA_LOAD.md        ← пошаговый план первичной загрузки исторических данных
├── TECH_DEBT.md                ← известные проблемы и технический долг
├── common/                     ← общая документация (актуально для всех окружений)
│   ├── 01_overview.md          ← обзор и назначение системы
│   ├── 02_architecture.md      ← архитектура воркеров и потоков данных
│   ├── 03_database.md          ← схема БД (market_analytics, market_analytics_jobs)
│   ├── analytics/              ← аналитика и бизнес-логика
│   │   └── indicators.md       ← реализованные технические индикаторы
│   └── auth/                   ← подсистема аутентификации и авторизации
│       ├── README_AUTH_SCHEMA.md
│       ├── README_AUTH_FLOWS.md
│       └── README_AUTH_IMPLEMENTATION_PLAN.md
├── dev/                        ← документация для разработки
│   ├── ARCHITECTURE_DEV.md     ← Ubuntu-хост, Docker на localhost, IDE-режим (Rider)
│   └── MIGRATE_TO_LINUX.md     ← чеклист переезда dev-окружения с Windows на Ubuntu
├── prod/                       ← документация для эксплуатации
│   ├── ARCHITECTURE_PROD.md    ← железо, сеть, состав сервисов
│   ├── 04_deployment.md        ← CI/CD, GitHub Actions, GHCR
│   └── 05_setup.md             ← пошаговая настройка прод-сервера
└── (будущее: staging/)         ← test/staging как зеркало прода

## С чего начать

| Кто ты | С чего читать |
|---|---|
| Новый разработчик | `common/01_overview.md` → `common/02_architecture.md` → `dev/ARCHITECTURE_DEV.md` |
| Разработчик настраивает локалку | `dev/ARCHITECTURE_DEV.md` |
| Хочешь понять схему БД | `common/03_database.md` |
| Хочешь понять индикаторы | `common/analytics/indicators.md` |
| Работаешь с auth | `common/auth/README_AUTH_SCHEMA.md` → `common/auth/README_AUTH_FLOWS.md` |
| Деплоишь на прод | `prod/04_deployment.md` |
| Настраиваешь прод-сервер с нуля | `prod/05_setup.md` → `Server_Network_Config.md` |
| Понять прод-инфраструктуру | `prod/ARCHITECTURE_PROD.md` |

## Соглашение

- **DEV ≠ PROD** — это сознательное разделение. DEV не содержит Cloudflare/Traefik/доменов.
- Документация по Docker-инфраструктуре конкретных compose-файлов — в `docker/docs/` (отдельная зона).
- Изменения в одном окружении (например, в DEV) **не должны** автоматически править другое окружение в документации.
