# Architecture Decision Records

Короткие записи о ключевых архитектурных решениях проекта: контекст, само решение, последствия и отвергнутые альтернативы. Формат намеренно сжатый — по одному решению на файл.

| #    | Решение                                                        |
|------|----------------------------------------------------------------|
| 0001 | [Dapper + хранимые процедуры вместо EF Core](./0001-dapper-vs-ef-core.md) |
| 0002 | [Разделение на два хоста: Worker и DataManager](./0002-worker-datamanager-split.md) |
| 0003 | [Модель очередей Hangfire с двумя серверами](./0003-hangfire-queue-model.md) |
| 0004 | [Watermarking и идемпотентность обработки](./0004-watermarking-idempotency.md) |
| 0005 | [`FOR UPDATE SKIP LOCKED` для выборки работы](./0005-for-update-skip-locked.md) |
| 0006 | [Помесячное партиционирование таблицы Trades](./0006-trades-monthly-partitioning.md) |
| 0007 | [Ротация по размеру диска и единая партиционная сетка](./0007-size-based-retention-and-unified-partitioning.md) |
| 0008 | [Отбор пар: фильтр по статусу торгов и гистерезис при снятии](./0008-symbol-selection-status-filter-and-hysteresis.md) |
| 0009 | [Очередь грязных минут вместо статус-колонки у тиков](./0009-dirty-minutes-queue.md) |
