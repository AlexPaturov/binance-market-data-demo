USE [MarketAnalytics]; -- Ваша база данных
GO

CREATE TABLE dbo.TrackedSymbols (
    Symbol VARCHAR(20) PRIMARY KEY,        -- Название пары, например 'BTCUSDT'
    IsActive BIT NOT NULL DEFAULT 1,       -- Флаг: отслеживаем ли мы ее сейчас (1 = да, 0 = нет)
    DateAdded DATETIME NOT NULL DEFAULT GETUTCDATE(), -- Когда пара была добавлена
    LastScanned DATETIME NULL              -- Когда мы в последний раз ее видели в ТОПе
);
GO

CREATE NONCLUSTERED INDEX IX_TrackedSymbols_IsActive
ON dbo.TrackedSymbols(IsActive);
GO

PRINT 'Таблица TrackedSymbols для хранения отслеживаемых пар создана.';