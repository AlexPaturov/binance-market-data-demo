# Приёмка demo-окружения

Чек-лист проверки `docker-compose.demo.yml` на каждой ОС. Общий поток одинаков; ниже него —
проверки, специфичные для Windows и macOS/arm64. Отмечайте пункты по мере прогона.

Предусловия: установлены Docker (Desktop или Engine) и git; dev-стек не запущен
(иначе заняты порты 5341 и 7002).

## Общий поток (каждая ОС)

- [ ] **1. Клонирование и запуск.**
  ```bash
  git clone <repo> && cd BinanceDataCollector/docker/compose
  cp .env.example .env
  docker compose -f docker-compose.demo.yml up -d --build
  ```
  Сборка проходит, стартуют 6 контейнеров.
- [ ] **2. Здоровье стека.** `docker compose -f docker-compose.demo.yml ps` → `bdc_demo_db` healthy, `bdc_demo_worker` и `bdc_demo_datamanager` в состоянии `Up`.
- [ ] **3. Init + seed.** `docker logs bdc_demo_db | grep "\[seed\] готово"` присутствует; в логе есть `CREATE TABLESPACE` без ошибок.
- [ ] **4. Вход — demo, не B2C.** `http://localhost:7002` открывает страницу выбора роли (Viewer / Operator / Admin), без редиректа на `b2clogin.com`.
- [ ] **5. Viewer — данные видны.** Вход Viewer → дашборд; на **Chart** график BTCUSDT за февраль 2026; панель **Months (Trades)** показывает `2026-02` как sealed.
- [ ] **6. Data Quality.** Проверка по BTCUSDT за февраль → группа «Свечи» `missing_minutes` = 0.
- [ ] **7. Разграничение ролей.** Operator: `http://localhost:7002/hangfire` → **403**. Admin: `/hangfire` → **200**.
- [ ] **8. Автономность.** `docker logs bdc_demo_worker | grep -i "Collectors:Enabled=false"` присутствует; обращений к Binance нет; консьюмеры `OhlcvAggregationService` и `FeatureCalculationService` стартовали. Отключение интернета работу не меняет.
- [ ] **9. Очистка.** `docker compose -f docker-compose.demo.yml down -v` удаляет контейнеры и тома.

## Linux (эталон)

- [ ] **10.** Полный общий поток 1–9 в одном `up`.

## Windows (Docker Desktop, WSL2)

- [ ] **11.** Общий поток 1–9.
- [ ] **12. LF в скриптах** (ключевое для этой ОС). Проверка: `git ls-files --eol docker/postgres/init/05_seed.sh` → `i/lf`. Признак нарушения: на шаге 3 в логе БД `05_seed.sh: not found` / `bad interpreter`, seed не грузится.
- [ ] **13. Тома.** БД поднимается на именованных томах (без bind на `C:\`).
- [ ] **14. Порт.** `http://localhost:7002` открывается из хостового браузера; порт 7002 свободен.

## macOS (Apple Silicon, arm64)

- [ ] **15.** Общий поток 1–9.
- [ ] **16. arm64 без эмуляции** (ключевое для этой ОС). `docker inspect bdc_demo_db --format '{{.Architecture}}'` → `arm64`; предупреждений `platform mismatch` нет.
- [ ] **17. cold-tablespace на именованном томе** (ключевое). `docker logs bdc_demo_db | grep "CREATE TABLESPACE"` — успешно (том `demo_cold` унаследовал каталог с правами postgres из образа). Признак нарушения: `could not set permissions` / `directory ... does not exist`.
- [ ] **18. pg_cron.** `docker exec bdc_demo_db psql -U bindatacoll -d market_analytics -c "SELECT count(*) FROM cron.job;"` → 1.

## Что уникально проверяет каждая ОС

| Проверка | Linux | Windows | macOS/arm64 |
|---|---|---|---|
| Общий поток 1–9 | да | да | да |
| LF в `.sh` (п. 12) | — | ключевое | — |
| arm64 без эмуляции (п. 16) | — | — | ключевое |
| cold-tablespace на томе (п. 17) | проверено | да | ключевое |

## Состояние проверки

На машине разработки (Linux) отдельно подтверждены: инициализация схемы + seed на чистом
томе (все счётчики строк, cold-tablespace, pg_cron, панель Months = февраль sealed), demo-вход
тремя ролями (Operator → 403 на /hangfire, Admin → 200), автономный старт worker без коллекторов,
поддержка arm64 у всех образов. Полный одновременный `up` шести сервисов и прогон на Windows и
macOS выполняются по этому чек-листу.
