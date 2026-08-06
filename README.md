# Binance Data Collector

Portfolio-проект: автономный конвейер рыночных данных Binance для бэктестинга и аналитики. Он собирает сделки и фичи стакана, строит свечи и индикаторы, проверяет качество данных и восстанавливает пропуски.

**Стек:** .NET 8 · PostgreSQL 16 + pg_cron · Dapper + SQL procedures · Hangfire · RabbitMQ · SignalR · Serilog/Seq · Docker Compose · xUnit/Testcontainers.

## Запустить demo

Нужен Docker. На чистом Linux используйте `./docker/demo-setup.sh`; на macOS — Docker Desktop.

```bash
git clone <your-fork-url>
cd BinanceDataCollector
./docker/demo-start.sh
```

Откройте `http://localhost:7002`, выберите роль и перейдите в **Chart**. Demo использует локальные seed-данные BTCUSDT за февраль 2026: сеть Binance и внешний провайдер входа не нужны. Логи доступны в Seq: `http://localhost:5341`.

Остановка:

```bash
./docker/demo-stop.sh
```

Подробности для Linux, macOS и Windows — [docs/dev/DEMO.md](./docs/dev/DEMO.md).

## Конвейер

```text
Binance WebSocket → Trades → DirtyMinutes → Ohlcv_1min → Ohlcv_Features
Binance WebSocket → order book in memory → OrderBook_Features
                       ↑
              audit and historical repair
```

- Выбирает ликвидные торгуемые пары и получает сделки по WebSocket.
- Агрегирует тики в минутные OHLCV и рассчитывает RSI, MACD и CVD.
- Сохраняет производные фичи стакана: imbalance, spread, depth и walls — без дорогого сырого L2.
- Использует `LISTEN/NOTIFY`, идемпотентную очередь и `FOR UPDATE SKIP LOCKED`, поэтому пересчёт безопасен при параллельной обработке и рестарте.
- Помесячно партиционирует растущие таблицы; аудит обнаруживает и заполняет пробелы.

## Устройство

Модульный монолит с двумя хостами:

- `Worker` — сбор, событийная обработка, аудит и фоновые задачи;
- `DataManager` — MVC-интерфейс, графики, архивы и панель качества данных;
- `Domain` → `Application` → `Infrastructure` — бизнес-модель, use cases и адаптеры к внешним системам.

Схема БД создаётся из [baseline](./docker/postgres/init/02_baseline.sql) при первом запуске demo-тома. Тесты запускаются так:

```bash
dotnet test BinanceDataCollector.sln
```

## Документация

- [Обзор](./docs/common/01_overview.md), [архитектура](./docs/common/02_architecture.md), [модель БД](./docs/common/03_database.md)
- [Ключевые архитектурные решения](./docs/adr/README.md)
- [Известные ограничения и дальнейшая работа](./docs/TECH_DEBT.md)
