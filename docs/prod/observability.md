# Наблюдаемость: метрики конвейера (Prometheus + Grafana)

Стек метрик прод-конвейера. Отвечает на вопрос «**как система себя чувствует во времени**» —
глубина очереди агрегации, отставание свечей и фич, загрузка диска, доля успешных проходов.
Дополняет Seq (логи, «что произошло»), не заменяет его.

> Смежное: [`ARCHITECTURE_PROD.md`](./ARCHITECTURE_PROD.md) — состав прод-стека ·
> [`../PLAN_OBSERVABILITY_EVENT_AGGREGATION.md`](../PLAN_OBSERVABILITY_EVENT_AGGREGATION.md) — план работ, в рамках которого стек поднят.

---

## Зачем

Инцидент 13–15.07.2026: агрегация свечей ушла в livelock (пачка в одной транзакции не
укладывалась в командный таймаут под нагрузкой импорта, откатывалась целиком, таймер
перезапускал то же самое). Диагностировать пришлось руками через `psql` и одноразовый
SSH-скрипт: ключевых чисел — глубины очереди, отставания свечи, доли успешных проходов —
не было видно нигде. Этот стек делает их постоянно видимыми, а класс проблем
«liveness под нагрузкой» — обнаружимым по тренду, а не по факту падения.

---

## Состав

Отдельный docker-compose-проект `bdc_monitoring`, поверх тех же внешних сетей, что и
app-стек. Поднимается и гасится независимо; экспортеры читают БД в read-only, приложение
и Postgres не трогаются.

| Контейнер | Образ | Роль |
| :--- | :--- | :--- |
| `bdc_prometheus` | `prom/prometheus:v2.54.1` | сбор и хранение метрик (retention 30 дней) |
| `bdc_node_exporter` | `prom/node-exporter:v1.8.2` | метрики хоста: диск (%util, iowait), CPU, память |
| `bdc_pg_exporter` | `postgres-exporter:v0.15.0` | прикладные метрики из `market_analytics` |
| `bdc_pg_exporter_jobs` | `postgres-exporter:v0.15.0` | прикладные метрики из `market_analytics_jobs` (Hangfire) |
| `bdc_grafana` | `grafana/grafana:11.2.0` | дашборд и (в перспективе) алерты |

**Почему два экспортера Postgres.** Прикладные числа лежат в двух разных БД: очередь и
отставания — в `market_analytics`, статистика проходов — в `market_analytics_jobs`. Один
экспортер подключается к одной БД, поэтому их два. Оба ходят напрямую в `bdc_db`
(не через PgBouncer — экспортеру нужна session-семантика, а не transaction pooling).

---

## Прикладные метрики

Считаются SQL-запросами через `--extend.query-path` экспортеров — **кода в приложении нет**.
Запросы: `docker/prometheus/queries_bdc.yaml` и `queries_jobs.yaml`.

| Метрика | Из чего | Смысл |
| :--- | :--- | :--- |
| `bdc_dirty_minutes_depth` | `count(*)` по `DirtyMinutes` | минут в очереди на агрегацию; норма — десятки |
| `bdc_candle_lag_seconds` | `now − max(OpenTime)` по `Ohlcv_1min` | отставание свежайшей свечи |
| `bdc_feature_lag_seconds` | то же по `Ohlcv_Features` | отставание фич (из них берётся CVD графика) |
| `bdc_aggregation_succeeded_15m` / `_failed_15m` | `hangfire.state` за 15 мин | успех/провал проходов агрегации |
| `bdc_aggregation_avg_duration_seconds` / `_max_...` | `PerformanceDuration` успешных за 15 мин | длительность прохода; прижатие к 600 = стена таймаута |
| `bdc_hangfire_queue_depth{queue}` | `hangfire.jobqueue` | глубины очередей Hangfire |

Метрики хоста — стандартные `node_*` (диск: `rate(node_disk_io_time_seconds_total[1m])*100`
для %util, `node_cpu_seconds_total{mode="iowait"}` для iowait).

---

## Дашборд

Grafana, папка **BinanceDataCollector**, дашборд «BDC — конвейер агрегации»
(`docker/grafana/dashboards/bdc-pipeline.json`, провижинится из файла — правит git, не UI).

Три плитки (текущая очередь, лаг свечи, доля успеха за 15 мин) и пять графиков: очередь
`DirtyMinutes` · отставание свечи и фич · диск %util и iowait · успех/провал агрегации ·
длительность прохода с линией-порогом на **600 секундах**. Последний график показывает
сигнатуру инцидента: успешные проходы, прижатые к стене таймаута.

Алерты пока не настроены (Grafana alerting — следующий шаг плана, этап 1.6).

---

## Доступ

Только через Tailscale (наружу, в Cloudflare, стек не публикуется):

| Сервис | Адрес |
| :--- | :--- |
| Grafana | `http://100.96.120.16:3000` — логин `admin`, пароль из `.env` |
| Prometheus | `http://100.96.120.16:9091` — таргеты и ad-hoc запросы (порт 9090 на хосте занят Cockpit) |

Пароль Grafana — переменная `GRAFANA_ADMIN_PASSWORD` в
`/opt/BinanceCollector/docker/compose/.env` (**в репозиторий не попадает**). Grafana
применяет её к учётке `admin` на каждом старте.

---

## Развёртывание и остановка

Конфиги в репозитории: `docker/prometheus/`, `docker/grafana/`,
`docker/compose/docker-compose.monitoring.prod.yml`. На сервер копируются вручную
(как и остальной прод-compose):

```bash
scp -P 2237 -r docker/prometheus docker/grafana lex@100.96.120.16:/opt/BinanceCollector/docker/
scp -P 2237 docker/compose/docker-compose.monitoring.prod.yml lex@100.96.120.16:/opt/BinanceCollector/docker/compose/

# на сервере, из каталога compose (там .env):
cd /opt/BinanceCollector/docker/compose
docker compose -p bdc_monitoring -f docker-compose.monitoring.prod.yml up -d
```

Остановка без следов (app-стек не затрагивается):

```bash
docker compose -p bdc_monitoring -f docker-compose.monitoring.prod.yml down
```

Данные Prometheus и Grafana — в named volumes `bdc_prometheus_data`, `bdc_grafana_data`;
`down` их не удаляет, `down -v` — удаляет.
