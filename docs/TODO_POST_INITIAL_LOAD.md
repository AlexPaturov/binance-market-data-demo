# TODO: После завершения initial data load

> **Статус на 2026-07-12.** Актуальный план — **[docs/mvp/mvp_execution_order.md](./mvp/mvp_execution_order.md)**. Здесь оставлено то, что ещё не сделано; выполненное помечено.
>
> Шаги 2 и 3 **выполнены**, но иначе, чем описано ниже: initial load не дожидались, диск переставили на прод в середине Фазы 3, а остаток загрузки продолжается на проде. Схема хранения на проде — bind mount `/mnt/ext/postgres_data` (см. `docker/docs/README_VOLUMES.md`). Описание шага 2 ниже устарело (упоминает VirtualBox VM, которой больше нет), оставлено как история.

---

## Шаг 0 — Пуш в master без запуска GitHub Actions

**Когда:** сразу после окончания initial load, до остального.

Добавить условие в `.github/workflows/deploy.yml` — пропускать деплой если в сообщении коммита есть `[skip ci]`.

Использование: коммиты с документацией, конфигами, hotfix-ами которые не требуют пересборки образов.

---

## Шаг 1 — Проверка целостности прод базы

Проверить полноту и связность загруженных данных:
- Нет ли дыр по символам и датам в `Trades`
- Соответствие `Ohlcv_1min` ← `Trades` (все свечи посчитаны)
- Соответствие `Ohlcv_Features` ← `Ohlcv_1min` (все индикаторы посчитаны)

**Требования к выводу:**
- Человекочитаемый отчёт (не сырой SQL)
- Регулируемый диапазон проверки (дата от/до)
- Детальный отчёт: какой символ, какой период, какой тип повреждения

**Проверить** есть ли уже достаточный механизм в `HistoricalAuditorWorker` / `QuickAuditorWorker` или нужно создавать отдельный инструмент.

---

## Шаг 2 — Переезд 4TB диска с дева на прод ✅ ВЫПОЛНЕНО (2026-07-12, иначе — см. статус выше)

**Контекст:** 4TB USB-C диск сейчас подключён к dev VM (`/mnt/ext`). После завершения initial load он освобождается для прода (GMKtec G2).

**Порядок:**
1. Остановить dev VM, отключить диск от VirtualBox USB Passthrough
2. Подключить диск к GMKtec физически
3. На GMKtec: смонтировать диск, добавить в `/etc/fstab` (UUID)
4. Перенести данные прод Postgres с named volume на диск (`rsync`)
5. Обновить `docker-compose.prod.yml`: заменить named volume `binancecollector_postgres_data` на bind mount
6. Проверить запуск прод Postgres

**Важно:** named volume `binancecollector_postgres_data` на GMKtec после переноса не удалять до подтверждения что bind mount работает корректно.

---

## Шаг 3 — Документирование изменений прода ✅ ВЫПОЛНЕНО (2026-07-12)

Обновить `docs/prod/ARCHITECTURE_PROD.md`:
- Добавить 4TB диск в описание инфраструктуры
- Обновить описание хранения Postgres (bind mount вместо named volume)
- Обновить `docker/docs/README_VOLUMES.md` и `README_DO_NOT_TOUCH.md`

---

## Шаг 4 — Запуск прода в рабочем режиме

- Включить recurring jobs (были отключены на время initial load — см. `docs/INITIAL_DATA_LOAD.md`)
- Проверить логи в Seq, убедиться что воркеры работают без ошибок
- Проверить что новые данные появляются в `Trades` и агрегируются в `Ohlcv_1min`

---

## Шаг 5 — Реализация rolling window sync (дев) — ОТЛОЖЕНО (dev сейчас на пустой локальной БД, см. ARCHITECTURE_DEV.md)

**Контекст:** описание и параметры в `docs/TECH_DEBT.md` → раздел «Dev/Prod синхронизация БД».

### 5А — Новый диск для dev VM

1. В VirtualBox: Settings → Storage → Add Hard Disk → Create VDI → Fixed size → 200 GB
2. В VM:
```bash
lsblk                          # найти новый диск (напр. /dev/sdc)
sudo mkfs.ext4 /dev/sdc
sudo mkdir -p /mnt/devdb
sudo blkid /dev/sdc            # взять UUID
echo 'UUID=xxx /mnt/devdb ext4 defaults 0 2' | sudo tee -a /etc/fstab
sudo mount -a
```
3. Обновить `docker-compose.db.yml` и `docker-compose.dev.yml`:
```yaml
- /mnt/devdb/postgres_data:/var/lib/postgresql/data
```

### 5Б — Первичное наполнение (разово, ~150 GB)

Через Tailscale, командой COPY напрямую между prod и dev БД:
```bash
psql -h 100.96.120.16 -U postgres market_analytics -c \
  "COPY (SELECT * FROM \"Trades\" WHERE \"TradeTime\" > extract(epoch from now() - interval '3 months')::bigint * 1000) TO STDOUT" \
  | psql -h 192.168.56.101 -p 5432 -U postgres market_analytics -c \
  "COPY \"Trades\" FROM STDIN"
```
То же для `Ohlcv_1min` и `Ohlcv_Features`. Разовая операция, займёт несколько часов.

### 5В — DevSyncService (инкрементальная синхронизация)

**Новая таблица `DevSyncWatermarks`** (только в dev БД, миграция):
```sql
CREATE TABLE "DevSyncWatermarks" (
    "Symbol"              VARCHAR(20) PRIMARY KEY,
    "LastSyncedTradeTime" BIGINT      NOT NULL,
    "LastSyncedAt"        TIMESTAMPTZ NOT NULL
);
```

**`DevSyncService : IHostedService`** — запускается на старте Worker'а:

Алгоритм:
1. Проверить `ASPNETCORE_ENVIRONMENT` — если не `Development`, завершить без действий
2. Подключиться к прод БД (connection string из `appsettings.Development.json`)
3. Для каждого символа: взять `LastSyncedTradeTime` из `DevSyncWatermarks`
4. Pull из прода: `WHERE Symbol = X AND TradeTime > watermark`
5. BulkInsert в dev
6. То же для `Ohlcv_1min` (по `OpenTime`) и `Ohlcv_Features`
7. Обновить watermark в `DevSyncWatermarks`
8. Rolling cleanup: `DELETE WHERE TradeTime < now() - interval '3 months'`
9. Сигнал завершения — Hangfire-серверы стартуют

**Условие запуска (env guard):**
```csharp
// Program.cs
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<DevSyncService>();
}
```
На проде сервис не регистрируется вообще — нулевой оверхед.

**Конфигурация** (`appsettings.Development.json`):
```json
"DevSync": {
  "Enabled": true,
  "ProdConnectionString": "Host=100.96.120.16;Port=5432;Database=market_analytics;Username=...;Password=...",
  "RollingWindowMonths": 3
}
```

**Порядок старта в `Program.cs`:**
```
DevSyncService.StartAsync() → sync завершён → Hangfire BackgroundServer → Worker'ы
```

Оценка трудоёмкости: **3-4 дня разработки** с учётом тестирования.

---

## Шаг 6 — Проверка целостности дев базы

После первичного наполнения (5Б) и после каждой синхронизации:
- Использовать тот же инструмент проверки целостности что в Шаге 1
- Диапазон проверки: последние 3 месяца

---

## История

- **2026-05-21:** план сформирован после завершения initial data load (40 пар, 16 месяцев).
