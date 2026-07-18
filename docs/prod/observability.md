# Наблюдаемость: метрики конвейера (Prometheus + Grafana)

Стек метрик прод-конвейера. Отвечает на вопрос «**как система себя чувствует во времени**» —
глубина очереди агрегации, приток и слив, отставание свечей и фич, загрузка диска.
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
| `bdc_grafana` | `grafana/grafana:11.2.0` | дашборд и алерт в Telegram |

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
| `bdc_pipeline_minutes_in_total` / `_out_total` | счётчики `n_tup_ins`/`n_tup_del` у `DirtyMinutes` | приток и слив очереди; `rate()` в Grafana даёт минут/мин |
| `bdc_pipeline_candles_written_total` / `_features_written_total` | `n_tup_ins+n_tup_upd` по партициям | темп записи свечей и фич |
| `bdc_hangfire_queue_depth{queue}` | `hangfire.jobqueue` | глубины очередей Hangfire (импорт, аудиты) |

Агрегация и расчёт фич — событийные сервисы, а не Hangfire-джобы ([ADR 0010](../adr/0010-event-driven-aggregation.md)), поэтому «доля успешных проходов» и «длительность прохода» больше не существуют — их место заняли счётчики потока `bdc_pipeline_*`. Здоровье конвейера видно по расхождению притока и слива очереди и по лагам, а не по исходу джобы.

Метрики хоста — стандартные `node_*` (диск: `rate(node_disk_io_time_seconds_total[1m])*100`
для %util, `node_cpu_seconds_total{mode="iowait"}` для iowait).

---

## Дашборд

Grafana, папка **BinanceDataCollector**, дашборд «BDC — конвейер агрегации»
(`docker/grafana/dashboards/bdc-pipeline.json`, провижинится из файла — правит git, не UI).

Три плитки (текущая очередь, лаг свечи, слив очереди минут/мин) и графики: очередь
`DirtyMinutes` · отставание свечи и фич · диск %util и iowait · приток/слив очереди ·
темп записи свечей и фич · глубины очередей Hangfire.

## Алерт

Одно правило (`docker/grafana/provisioning/alerting/rules.yml`, из файла, не из UI):
**«Лаг свечи выше 10 минут»** — `bdc_candle_lag_seconds > 600` устойчиво `for: 5m`.
Лаг свечи — сквозной индикатор: он растёт при любом отказе на пути тик → свеча.
`noDataState`/`execErrState` = `Alerting`: молчащий экспортёр или лежащая база — тоже
«свечи не считаются», тишина не считается здоровьем. Глубина очереди в алерт не годится:
под импортом она растёт по построению, а реалтайм при этом здоров.

Доставка — **Telegram** (`contactpoints.yml` + `policies.yml`): бот `@bdc_alerts_bot`,
токен и chat_id из GitHub Secrets → `.env` (в репозитории значений нет).

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
