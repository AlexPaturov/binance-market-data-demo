SELECT 'CREATE DATABASE market_analytics_jobs'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'market_analytics_jobs')\gexec