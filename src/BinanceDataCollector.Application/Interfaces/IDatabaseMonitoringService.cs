using BinanceDataCollector.Application.Models;
using BinanceDataCollector.Application.ViewModels;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Предоставляет методы для мониторинга состояния базы данных.
/// </summary>
public interface IDatabaseMonitoringService
{
    /// <summary>
    /// Получает текущий размер указанной базы данных
    /// </summary>
    /// <param name="databaseName">Имя базы данных.</param>
    /// <returns>Строка с размером базы данных.</returns>
    Task<string> GetDatabaseSizeAsync(string databaseName);
    
    /// <summary>
    /// Асинхронно получает список активных подключений к серверу PostgreSQL,
    /// сгруппированных по имени приложения, базе данных и состоянию.
    /// </summary>
    /// <remarks>
    /// Этот метод запрашивает системное представление pg_stat_activity.
    /// </remarks>
    /// <returns>
    /// Задача, представляющая асинхронную операцию.
    /// Результат задачи содержит список объектов <see cref="PostgresConnectionInfo"/>.
    /// </returns>
    Task<List<PostgresConnectionInfo>> GetActiveConnectionsAsync();
    
    /// <summary>
    /// Асинхронно получает комплексную информацию о состоянии указанной базы данных.
    /// Включает в себя общий размер, размеры таблиц и индексов, а также информацию об активных подключениях.
    /// </summary>
    /// <param name="databaseName">Имя базы данных, для которой необходимо получить детали.</param>
    /// <returns>
    /// Задача, представляющая асинхронную операцию.
    /// Результат задачи содержит объект <see cref="DatabaseDetailsViewModel"/> с полной информацией о базе данных.
    /// </returns>
    Task<DatabaseDetailsViewModel> GetDatabaseDetailsAsync(string databaseName);

    /// <summary>
    /// Помесячная сводка по партициям Trades: tablespace (hot SSD / cold HDD) и печать месяца.
    /// Отдельный метод — под панель с собственным авто-обновлением. Осмысленна только для
    /// market_analytics; для прочих БД возвращает пустой список.
    /// </summary>
    Task<List<MonthPartitionInfo>> GetMonthPartitionsAsync(string databaseName);
}