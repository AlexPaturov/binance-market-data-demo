# Документация

## Структура

```text
docs/
├── INDEX.md               ← этот файл
├── TECH_DEBT.md           ← известные ограничения и дальнейшая работа
├── adr/                   ← ключевые архитектурные решения
├── common/                ← обзор, архитектура и модель данных
│   ├── 01_overview.md
│   ├── 02_architecture.md
│   ├── 03_database.md
│   └── analytics/         ← индикаторы и проверки качества
└── dev/
    └── DEMO.md            ← запуск demo на Linux, macOS и Windows

docker/postgres/           ← PostgreSQL image, baseline и seed-данные
```

## С чего начать

| Задача | Куда смотреть |
|---|---|
| Запустить проект | [`dev/DEMO.md`](./dev/DEMO.md) |
| Понять систему | [`common/01_overview.md`](./common/01_overview.md) → [`common/02_architecture.md`](./common/02_architecture.md) |
| Разобраться в схеме БД | [`common/03_database.md`](./common/03_database.md) |
| Понять решения | [`adr/README.md`](./adr/README.md) |
| Посмотреть ограничения | [`TECH_DEBT.md`](./TECH_DEBT.md) |

Документация описывает демонстрационную сборку. Все материалы, не относящиеся к воспроизводимому demo-проекту, намеренно исключены из portfolio-ветки.
