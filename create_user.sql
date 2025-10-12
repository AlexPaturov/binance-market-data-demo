-- =====================================================================
-- Скрипт для создания пользователя (роли) 'bindatacoll'
-- =====================================================================

-- Устанавливаем переменные для удобства
DO $$
DECLARE
db_user TEXT := 'bindatacoll';
    db_password TEXT := 'dt_hfgd_yyyd'; -- Важно: Замени на реальный пароль из appsettings/docker-compose
BEGIN
    -- Проверяем, существует ли уже роль с таким именем
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = db_user) THEN
        -- Если не существует - создаем
        -- CREATE ROLE - более современный и гибкий синоним для CREATE USER
        EXECUTE format('CREATE ROLE %I WITH LOGIN PASSWORD %L', db_user, db_password);
        RAISE NOTICE 'Роль "%" успешно создана.', db_user;
ELSE
        -- Если существует - просто сообщаем об этом
        RAISE NOTICE 'Роль "%" уже существует, создание пропущено.', db_user;
END IF;

    -- Выдаем права на подключение к базам данных
    -- (Сами базы должны уже существовать)
EXECUTE format('GRANT CONNECT ON DATABASE market_analytics TO %I', db_user);
EXECUTE format('GRANT CONNECT ON DATABASE market_analytics_jobs TO %I', db_user);

-- Выдаем права на создание объектов (таблиц, и т.д.) в схеме 'public'
-- Это нужно, чтобы скрипты создания схемы отработали корректно
EXECUTE format('GRANT CREATE, USAGE ON SCHEMA public TO %I', db_user);

RAISE NOTICE 'Необходимые права для роли "%" выданы.', db_user;

END
$$;