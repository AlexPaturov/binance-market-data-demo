# Документация

## Структура

```
docs/
├── INDEX.md               ← этот файл
├── TECH_DEBT.md           ← что известно и не сделано
├── PLAN_DEMO_ENVIRONMENT.md ← рабочий план demo-окружения
├── adr/                   ← ключевые архитектурные решения и почему они такие
├── common/                ← актуально для всех окружений
│   ├── 01_overview.md     ← что это и зачем
│   ├── 02_architecture.md ← воркеры, потоки данных, обработка ошибок
│   ├── 03_database.md     ← схема БД, процедуры, партиционирование
│   ├── analytics/
│   │   ├── indicators.md    ← индикаторы и фичи стакана
│   │   └── data_quality.md  ← проверки качества данных
│   └── auth/              ← спецификация полной IAM-схемы (для будущего форка)
├── dev/
│   ├── ARCHITECTURE_DEV.md
│   └── DEMO_ACCEPTANCE.md   ← чек-лист приёмки demo-окружения (Linux/Windows/macOS)
└── prod/
    ├── ARCHITECTURE_PROD.md ← железо, сеть, хранилища, запуск/остановка
    ├── network.md           ← UFW, Tailscale, Docker-сети, порты
    ├── observability.md     ← метрики конвейера: Prometheus + Grafana
    ├── 04_deployment.md     ← CI/CD, GitHub Actions, GHCR
    └── 05_setup.md          ← настройка сервера с нуля

docker/docs/               ← эксплуатация Docker-стека
├── README_VOLUMES.md      ← где лежат данные и что нельзя трогать
└── README_DOCKER_RUN.md   ← команды compose для dev

docker/postgres/README.md  ← схема БД: baseline, миграции, как менять (ADR 0013)
```

## С чего начать

| Задача | Куда смотреть |
|---|---|
| Понять, что это за проект | [`common/01_overview.md`](./common/01_overview.md) → [`common/02_architecture.md`](./common/02_architecture.md) |
| Поднять локалку | [`dev/ARCHITECTURE_DEV.md`](./dev/ARCHITECTURE_DEV.md) |
| Разобраться в схеме БД | [`common/03_database.md`](./common/03_database.md) |
| Понять, **почему** так сделано | [`adr/README.md`](./adr/README.md) |
| Индикаторы и фичи стакана | [`common/analytics/indicators.md`](./common/analytics/indicators.md) |
| Проверить качество данных | [`common/analytics/data_quality.md`](./common/analytics/data_quality.md) |
| Задеплоить | [`prod/04_deployment.md`](./prod/04_deployment.md) |
| Посмотреть метрики конвейера | [`prod/observability.md`](./prod/observability.md) |
| Запустить или потушить прод | [`prod/ARCHITECTURE_PROD.md`](./prod/ARCHITECTURE_PROD.md#6-запуск-и-остановка) |
| Настроить сервер с нуля | [`prod/05_setup.md`](./prod/05_setup.md) → [`prod/network.md`](./prod/network.md) |

## Соглашения

- **DEV ≠ PROD.** В dev нет Cloudflare, Traefik и доменов. Изменение в одном окружении не правит документацию другого автоматически.
- **Документация описывает то, что есть.** Планы и незакрытые вопросы живут в [`TECH_DEBT.md`](./TECH_DEBT.md), обоснования решений — в [`adr/`](./adr/).
- `docs/common/auth/` — спецификация полной IAM-схемы для **будущего отдельного проекта**. Это не описание текущего кода: сейчас работает только Azure B2C с ролями из claims.
