https://192.168.0.200:9090/system  - терминал сервера 
http://localhost:5341/#/events?range=1d - локальный лог
http://100.96.120.16:5341  - лог сервера (admin, as34zx67)

# старт стоп контейнер с приложением
# cd /opt/BinanceCollector
# sudo docker compose stop binance_collector_app
# sudo docker compose start binance_collector_app
# sudo systemctl restart docker - рестарт докера при переходе на новый источник wi-fi/lan

## ---- как выполнять долгие команды с использованием виртуальных терминалов начало -------------------------------------------------------------------
# 1. Подключаемся по SSH терминалу
# 2. Заходим в папку с docker контейнером в котором находится наша база данных
# 3. Запускаем новую сессию screen и даем ей имя, например - vacuum_session
screen -S vacuum_session

# 4.1. Внутри этой новой сессии запускаем psql и вашу команду
psql -h localhost -U bindatacoll -d market_analytics -c "VACUUM FULL VERBOSE public.\"Trades\";"

# 4.2.1. Если работаем в докере 
docker exec -it binance_postgres psql -U bindatacoll -d market_analytics
# 4.2.2. Пример команды 
CREATE INDEX CONCURRENTLY IX_Trades_TradeTime_Desc ON public."Trades" ("TradeTime" DESC);

# 5. Теперь можно "отцепиться" от этой сессии, нажав Ctrl+A, а затем D.
#    Процесс продолжит работать в фоне.
# 6. Вы можете закрыть SSH, пойти спать.
# 7. Чтобы вернуться в сессию и посмотреть, что там происходит:
screen -r vacuum_session
## ---- как выполнять долгие команды с использованием виртуальных терминалов окончание -----------------------------------------------------------------

## ------------- выгрузка схемы базы данных начало ----------------------------
# 1. Устанавливаем пароль в переменную окружения
$env:PGPASSWORD="dt_hfgd_yyyd"
pg_dump -U bindatacoll -h localhost -p 5432 --schema-only --schema=public -d market_analytics -f "D:\pg_script\market_analytics_dev.sql"
pg_dump -U bindatacoll -h localhost -p 5432 --schema-only --schema=hangfire -d market_analytics_jobs_dev -f "D:\pg_script\market_analytics_jobs_dev.sql"
## ------------- выгрузка схемы базы данных окончание -------------------------

## ----------------- полная очистка базы перед загрузкой новых данных начало ---------------------------------------------------------------------------
-- Отключаем триггеры (если они есть), чтобы ускорить удаление
SET session_replication_role = 'replica';

-- Очищаем все таблицы с бизнес-данными.
-- TRUNCATE работает гораздо быстрее, чем DELETE, так как не сканирует строки.
TRUNCATE TABLE public."Trades" RESTART IDENTITY CASCADE;
TRUNCATE TABLE public."Ohlcv_1min" RESTART IDENTITY CASCADE;
TRUNCATE TABLE public."Ohlcv_Features" RESTART IDENTITY CASCADE;
TRUNCATE TABLE public."Audit_Blocks" RESTART IDENTITY CASCADE;
TRUNCATE TABLE public."HistoricalAudit_Watermarks" RESTART IDENTITY CASCADE;

-- Возвращаем триггеры в нормальный режим
SET session_replication_role = 'origin';

-- Сообщаем об успехе
SELECT 'Таблицы с данными успешно очищены.' AS status;

### -- Очищаем таблицы Hangfire. Порядок важен из-за внешних ключей.
TRUNCATE TABLE hangfire."jobparameter" CASCADE;
TRUNCATE TABLE hangfire."jobqueue" CASCADE;
TRUNCATE TABLE hangfire."state" CASCADE;
TRUNCATE TABLE hangfire."list" CASCADE;
TRUNCATE TABLE hangfire."hash" CASCADE;
TRUNCATE TABLE hangfire."set" CASCADE;
TRUNCATE TABLE hangfire."counter" CASCADE;
TRUNCATE TABLE hangfire."aggregatedcounter" CASCADE;
TRUNCATE TABLE hangfire."job" CASCADE;
TRUNCATE TABLE hangfire."server" CASCADE;

SELECT 'Таблицы Hangfire успешно очищены.' AS status;


## ----------------- полная очистка базы перед загрузкой новых данных окончание -------------------------------------------------------------------------

## ---------------- установка ватермарок начало ---------------------------------------------------------------------------------------------------------
DO $$
DECLARE
    -- УСТАНОВИ ЗДЕСЬ ДАТУ, С КОТОРОЙ НАЧНЕТСЯ ТВОЯ ИСТОРИЯ
    start_date_literal TEXT := '2025-01-01'; -- <-- ИЗМЕНИ ЗДЕСЬ
    
    start_date TIMESTAMP := to_timestamp(start_date_literal, 'YYYY-MM-DD') AT TIME ZONE 'UTC';
    start_timestamp BIGINT;
BEGIN
    start_timestamp := (EXTRACT(EPOCH FROM start_date) * 1000)::BIGINT;

    INSERT INTO public."Processing_Watermarks" ("ProcessName", "LastProcessedTimestamp", "Status", "LastUpdate_UTC")
    VALUES
    ('OhlcvAggregator', start_timestamp, 'Idle', NOW()),
    ('FeatureCalculator', start_timestamp, 'Idle', NOW());
    -- ON CONFLICT ... (оставляем)
END $$;
## ---------------- установка ватермарок окончание -------------------------------------------------------------------------------------------------------

## ---------------- RabbitMQ начало ----------------------------------------------------------------------------------------------------------------------
# By default user name: guest, password: guest
# If you forgot password, try to create a new user as below

rabbitmqctl add_user myuser mypassword
rabbitmqctl set_user_tags myuser administrator
rabbitmqctl set_permissions -p / myuser ".*" ".*" ".*"

## ---------------- RabbitMQ окончание -------------------------------------------------------------------------------------------------------------------

## Посмотреть кто держит процесс ОС
sudo lsof -i :5432

sudo chown -R $USER:$USER ~/etc/pgbouncer/
- права на папку для текущего пользователя

-- delete the folder
sudo rm -rf /etc/pgbouncer


### ------------------ Docker commands
1. docker ps -a   -- to show which containers was run
2. docker logs binance_pgbouncer -- Проверить логи
3. docker logs binance_postgres -- Проверить логи
4. docker ps -a -- Посмотри, какие контейнеры есть
5. docker stop <container_name> -- Останови контейнер
6. docker rm <container_name> -- Удалить контейнер
7. docker rmi datalust/seq -- Удали image, на котором он основан
8. docker ps -a        # контейнеры
9. docker images       # образы
10. docker volume ls    # тома
11. docker network ls   # сети 
12. docker volume rm <volume_name> -- Удалить конкретный том
13. 

- passw seq qw12qw12 24.10.2025
- docker stop $(docker ps -q) -- остановка всех контейнеров
- docker rm $(docker ps -aq)

  ----------- заходим внутрь контейнера pgbouncer ---------------
- # Запустите контейнер (если не запущен)
docker compose up -d pgbouncer

# Зайдите внутрь контейнера
docker exec -it binance_pgbouncer sh

# Найдите pgbouncer.ini
find / -name "pgbouncer.ini" 2>/dev/null

# Обычно он здесь:
cat /etc/pgbouncer/pgbouncer.ini

# Выйдите
exit

----------- заходим внутрь контейнера binance_postgres ---------------
# Зайдите внутрь контейнера
docker exec -it binance_postgres sh

--------------------------
show logs
docker logs bdc_worker --tail 50

-------------------------------------------------
смотрим все сети докера
docker network ls
--------------------------------------------------
посмотреть сети
docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{println $k}}{{end}}' bdc_worker
----------------------------------------------------------------------------------------------------------

# -------------------- Найти PID Worker
pidof BinanceDataCollector.DataManager

# установить
dotnet tool install -g dotnet-gcdump

# Сделать первый снимок
dotnet-gcdump collect -p 384732 -o dumpDM_1.gcdump
dotnet-dump collect -p 384732 -o worker-dump.dmp

# Подождать 30-60 секунд (пока память растет)

# Сделать второй снимок
dotnet-gcdump collect -p <PID> -o snapshot2.gcdump

# Открыть дамп для анализа
dotnet-dump analyze worker-dump.dmp

dumpheap -type System.Byte[]

# 3. Найти КТО ДЕРЖИТ ссылку (ЭТО ПОКАЖЕТ МЕТОД!)
gcroot <адрес_объекта>
gcroot  7f36b8f2d7c8

gcroot <АДРЕС>

# 2. Открыть в heapview
dotnet-heapview dumpDM_1.gcdump

# ----------------------------- firewall begin ---------------------------------------------
# Для PgBouncer
sudo ufw allow 6432/tcp

# Для прямого доступа к PostgreSQL (наш "черный ход")
sudo ufw allow 5433/tcp

# Для RabbitMQ (клиентский порт)
sudo ufw allow 5672/tcp

# Для админки RabbitMQ
sudo ufw allow 15672/tcp

# Для админки Seq (уже есть, но повторить не вредно)
sudo ufw allow 5341/tcp
# ----------------------------- firewall end -----------------------------------------------

# ----------------------------- app domain -----------------------------------------------
Bought Nov 01, 2025.
Renewal Nov 01, 2026.
jahasim.com
# ----------------------------- app domain -----------------------------------------------

# ----------------------------- Проверка проекта на сервере -----------------------------------------------

# На сервере, в папке с проектом
docker compose -f docker-compose.prod.yml up -d
Проверка:
Откройте в браузере https://hangfire.jahasim.com.
Откройте https://seq.jahasim.com.
Откройте https://datamanager.jahasim.com.



