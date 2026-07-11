# Portfolio MVP Assessment

## 1. Что делает проект и домен

`BinanceDataCollector` - проект в домене market data / crypto analytics.

Он собирает данные Binance, хранит сделки и свечи в PostgreSQL, импортирует исторические архивы, агрегирует OHLCV, считает признаки/индикаторы и имеет backoffice UI для управления и наблюдения.

Как портфолио это не CRUD, а data ingestion + background processing + database-heavy backend.

## 2. Текущий стек и архитектурный стиль

### MUST HAVE для описания

- C# / .NET 8, ASP.NET Core, Worker services.
- PostgreSQL 16, Dapper, SQL stored procedures, partitioning.
- Hangfire для фоновых задач и очередей.
- RabbitMQ + SignalR для статусов.
- Serilog + Seq.
- Docker / Docker Compose, PgBouncer, Traefik/Cloudflare в prod-контуре.
- xUnit/Moq/Testcontainers частично.
- Архитектурно: layered architecture / Clean-ish layering: `Domain`, `Application`, `Infrastructure`, `Worker`, `DataManager`.

Важно: это не микросервисы. Лучше говорить "modular monolith with separate worker/web hosts".

## 3. Что уже реализовано и выглядит демонстрируемым

### MUST HAVE уже в хорошем состоянии

- Разделение слоев и зависимостей: интерфейсы в Application, реализации в Infrastructure, orchestration в Worker.
- Hangfire orchestration с отдельными очередями и серверами: `src/BinanceDataCollector.Worker/Program.cs`.
- Архивный импорт Binance ZIP/CSV через streaming API: `src/BinanceDataCollector.Infrastructure/Services/ArchiveService.cs`.
- Batch insert / DB-side processing через Dapper и SQL procedures: `src/BinanceDataCollector.Infrastructure/Persistence/Repositories/TradeRepository.cs`.
- Atomic claim pattern через `FOR UPDATE SKIP LOCKED`: `src/BinanceDataCollector.Infrastructure/Persistence/Repositories/OhlcvRepository.cs`.
- OHLCV aggregation с watermark и fail-fast behavior: `src/BinanceDataCollector.Worker/Workers/OhlcvAggregatorWorker.cs`.
- Расчет индикаторов через библиотеку, не hand-rolled math: `src/BinanceDataCollector.Application/Analytics/IndicatorService.cs`.
- Docker multi-stage build для двух приложений: `docker/Dockerfile`.
- Документация уже есть и выглядит серьезно: `README.md`, `docs/common/02_architecture.md`, `docs/common/03_database.md`.

## 4. Что сейчас мешает считать проект MVP

### MUST HAVE blockers

- Основные recurring jobs сейчас сняты с расписания during initial load: `src/BinanceDataCollector.Worker/Common/HangfireJobsService.cs`.
- Realtime collector зарегистрирован, но hosted service закомментирован: `src/BinanceDataCollector.Worker/Program.cs`.
- Схема БД по документации не полностью воспроизводима из репозитория: это сильный portfolio-risk.
- CI/CD не запускает тесты и деплоит на `pull_request`: `.github/workflows/deploy.yml`.

Решено (шаги 1–2 MVP):

- Авторизация: middleware включён, роли `Viewer/Operator/Admin` работают, Hangfire закрыт под Admin (`src/BinanceDataCollector.DataManager/Program.cs`), проверено авто + ручной E2E — см. `docs/mvp/auth_verification.md`.
- Тесты честные и зелёные: `dotnet test BinanceDataCollector.sln` → 22 passed, 0 skipped. `FeatureCalculatorWorkerTests` переписан в реальный тест оркестрации `DoWorkAsync`; пустой скаффолд `Infrastructure.Tests` удалён.

## 5. Обязательные элементы MVP для Senior C#/.NET портфолио

### MUST HAVE

- Репозиторий должен собираться одной командой: `dotnet build BinanceDataCollector.sln`.
- Тестовый контур должен быть честным: либо `dotnet test` green, либо сломанные/черновые тесты удалены/помечены так, чтобы CI не запускал их как готовые.
- Один end-to-end сценарий должен быть воспроизводим из README: например `download one daily archive -> parse -> bulk insert -> aggregate OHLCV -> calculate features`.
- Актуальный SQL baseline должен быть в репозитории. Не обязательно идеальные миграции, но должно быть понятно, как поднять schema-from-scratch.
- README должен честно описывать текущий MVP state: что работает, что отключено intentionally, как запустить demo.
- Нужно убрать или явно изолировать опасные portfolio-сигналы: открытый Hangfire dashboard, PR deploy, секреты/debug logging в deploy.
- Документация должна объяснять ключевые решения, а не обещать production-perfect систему.

### SHOULD HAVE

- Один Testcontainers integration test на реальный repository path.
- Один unit test на worker orchestration.
- Скриншоты/короткий demo flow для DataManager/Hangfire/Seq.
- Небольшая ADR-документация по worker queues, watermarking, DB-heavy aggregation.

## 6. Что можно явно НЕ делать до собеседований

### DO NOT DO NOW

- Не строить полноценную production migration system, если есть один надежный schema baseline.
- Не внедрять EF Core "для красоты". Dapper + SQL здесь оправдан.
- Не дописывать ML/strategy/backtesting.
- Не расширять UI.
- Не доделывать полноценный auth/roles/scopes, если проект демонстрируется локально.
- Не превращать Worker в набор микросервисов.
- Не чинить весь `TECH_DEBT.md`.
- Не делать идеальный мониторинг, алерты, SLO, distributed tracing.
- Не переписывать доменную модель под DDD ради интервью.

## 7. Файлы и модули, которые лучше всего демонстрируют уровень

1. `src/BinanceDataCollector.Infrastructure/Persistence/Repositories/TradeRepository.cs` - bulk insert, DB procedures, data access pragmatism.
2. `src/BinanceDataCollector.Infrastructure/Persistence/Repositories/OhlcvRepository.cs` - claim/lock pattern.
3. `src/BinanceDataCollector.Worker/Workers/OhlcvAggregatorWorker.cs` - watermark-driven background processing.
4. `src/BinanceDataCollector.Worker/Workers/FeatureCalculatorWorker.cs` - batch processing + indicators + CVD enrichment.
5. `src/BinanceDataCollector.Infrastructure/Services/ArchiveService.cs` - streaming archive parsing.
6. `src/BinanceDataCollector.Worker/Workers/Archives/OnlineArchiveImportWorker.cs` - Hangfire archive import pipeline.
7. `src/BinanceDataCollector.Infrastructure/BinanceClient/BinanceService.cs` - external API adapter.
8. `src/BinanceDataCollector.Worker/Program.cs` - composition root, logging, health checks, Hangfire.
9. `docker/Dockerfile` - multi-stage deployment.
10. `docs/common/02_architecture.md` - system-level explanation.

## 8. Архитектурные решения, которые стоит описать в markdown-документации

### MUST HAVE docs

- Почему Dapper + stored procedures вместо EF Core.
- Почему ingestion и UI разделены на Worker/DataManager hosts.
- Hangfire queue model: realtime / quick audit / historical / archive import.
- Watermarking и idempotency.
- Почему `FOR UPDATE SKIP LOCKED` используется для feature calculation.
- Data lifecycle: archive import -> trades -> OHLCV -> features -> quality checks.
- Current MVP state: какие jobs включены, какие отключены и почему.
- Local demo setup: минимальный сценарий без production-server assumptions.

## 9. Риски и слабые места, которые интервьюер может заметить

- Тесты сейчас красные. Это самый заметный риск.
- В репозитории есть черновики, TODO, закомментированные jobs и псевдотесты.
- `DataManager` ссылается на `Worker` project, что выглядит как нарушение границ.
- Состояние DB schema не выглядит полностью управляемым из repo.
- Auth частично настроен, но middleware выключен.
- CI/CD выглядит опасно для реального production: PR deploy, нет `dotnet test`, debug output `.env`.
- В `OnlineArchiveImportWorker.cs` local-file import flush делает insert практически на каждой записи, это может вызвать вопрос о внимательности.
- В репозитории есть сгенерированные frontend libs и много SQL/support артефактов, что снижает чистоту просмотра.
- Не видно из репозитория, что production реально сейчас стабильно работает.
- Не видно из репозитория актуального успешного GitHub Actions run.

## 10. Финальный чеклист STOP CONDITION

Остановить проект и перейти к подготовке по C# после выполнения только этих пунктов.

### MUST HAVE

- `dotnet build BinanceDataCollector.sln` проходит.
- `dotnet test` проходит, либо в solution оставлены только честные green tests.
- Удалены/исключены псевдотесты и пустые тестовые классы.
- В репо есть актуальный SQL baseline для demo DB.
- README содержит один воспроизводимый demo сценарий.
- Документация явно говорит: realtime collector / audit / feature jobs включены или intentionally disabled for MVP.
- CI хотя бы build+test без deploy на PR.
- Секреты/debug `.env` output в workflow убраны или workflow помечен как non-demo/prod-private.
- Подготовлен список из 5-7 архитектурных решений, которые можно объяснить за 10 минут.

### SHOULD HAVE

- Один нормальный Testcontainers test на repository/SQL path.
- Один screenshot или короткое описание Hangfire/DataManager demo.
- Короткий `docs/PORTFOLIO_MVP.md`.

### DO NOT DO NOW

- Не закрывать весь `TECH_DEBT.md`.
- Не достраивать production-grade auth.
- Не добавлять новые фичи.
- Не переписывать архитектуру.
- Не расширять домен дальше market data ingestion.

## Итоговая оценка

Проект уже достаточно сильный по масштабу и домену для Senior C#/.NET портфолио, но пока выглядит как active workbench, а не остановленный MVP.

Граница MVP здесь не в новых фичах, а в воспроизводимости, честных тестах, актуальной схеме БД и явном описании текущего состояния.
