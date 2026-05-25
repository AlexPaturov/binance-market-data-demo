# Data Quality — Layer 1: целостность сырых данных

Этот документ описывает систему проверки целостности тиковых данных (таблица `Trades`).

---

## Зачем

Все производные показатели (OHLCV, RSI, MACD, CVD) вычисляются поверх сырых тредов. Если тиковые данные кривые — все индикаторы будут кривыми тоже, просто это будет незаметно. Layer 1 проверяет данные **до** расчёта индикаторов, на уровне сырых значений.

---

## Что проверяется

Проверки выполняются **по каждому символу** за указанный месяц:

| Проверка | Описание | Влияние на статус |
|---|---|---|
| **TradeCount** | Количество тредов в периоде | 0 → `error` |
| **GapCount** | Пробелы в последовательности `TradeId` | > 0 → `warning` |
| **InvalidPriceCount** | Треды с `Price <= 0` или `Quantity <= 0` | > 0 → `error` |
| **OutlierCount** | Цена отклоняется от среднего более чем на 5σ | > 0 → `warning` |

### Логика статусов

- **`error`** — данные непригодны для расчёта индикаторов. Нужно разобраться и перегрузить.
- **`warning`** — данные частично неполные (пропуски в ID) или содержат аномалии. Индикаторы считаются, но с оговорками.
- **`ok`** — всё чисто.

> **Про GapCount:** пробел в `TradeId` означает, что трейды с этими ID у нас отсутствуют. Для Binance это нормально — биржа иногда пропускает ID. Маленькое количество пробелов — warning, не error.

---

## Как запустить

### Из Hangfire Dashboard

> Использовать дашборд **Worker** (`:7001`), не DataManager (`:7002`).
> DataManager видит все джобы в общей БД, но не может десериализовать Worker-типы.

1. Открыть `http://localhost:7001/hangfire`
2. Перейти в **Jobs → Recurring** или **Jobs → Create**
3. Тип: `BinanceDataCollector.Worker.Workers.DataQualityWorker`
4. Метод: `CheckMonthAsync`
5. Параметры: `year=2025, month=4`

Или через **Enqueue** прямо из дашборда — воркер появится в очереди `default`.

### Программно (из другого джоба)

```csharp
BackgroundJob.Enqueue<DataQualityWorker>(w => w.CheckMonthAsync(2025, 4));
```

---

## Где смотреть результаты

### Seq

Каждый символ логируется отдельной строкой. Фильтр в Seq:

```
@Level = 'Error' and SourceContext like '%DataQualityWorker%'
```

### База данных

Таблица `public."DataQualityReports"`:

```sql
SELECT * FROM "DataQualityReports"
WHERE "PeriodMonth" = '2025-04-01'
ORDER BY "Status" DESC, "Symbol";
```

Сводка по месяцу:

```sql
SELECT "Status", COUNT(*) AS symbols
FROM "DataQualityReports"
WHERE "PeriodMonth" = '2025-04-01'
GROUP BY "Status";
```

Все проблемные символы:

```sql
SELECT "Symbol", "PeriodMonth", "TradeCount", "GapCount", "InvalidPriceCount", "OutlierCount"
FROM "DataQualityReports"
WHERE "Status" <> 'ok'
ORDER BY "PeriodMonth", "Symbol";
```

---

## Архитектура

```
DataQualityWorker.CheckMonthAsync(year, month)
    │
    ├── ITrackedSymbolRepository.GetActiveSymbolsAsync()   — список активных символов
    │
    └── для каждого символа:
            IDataQualityRepository.CheckSymbolMonthAsync() — один SQL-запрос (CTE)
            IDataQualityRepository.UpsertReportAsync()     — сохраняет/обновляет отчёт
```

Проверка реализована как **один SQL CTE-запрос** по партиции `Trades_YYYY_MM`:
- `LAG(TradeId)` — считает пробелы в последовательности ID
- `STDDEV(Price)` — база для 5-sigma outlier detection
- Все метрики за один проход по данным

Таблица `DataQualityReports` использует upsert по `(Symbol, PeriodMonth)` — повторный запуск обновляет существующий отчёт.

---

## Когда запускать

| Ситуация | Действие |
|---|---|
| После загрузки данных за месяц | Запустить `CheckMonthAsync(year, month)` |
| Перед переносом партиций на прод | Убедиться что все символы `ok` или разобрать `warning`/`error` |
| После перезаливки данных (re-import) | Повторный запуск обновит отчёт |

---

## Миграция

Таблица создаётся из: `sqlScripts/migrations/002_data_quality_reports.sql`
