namespace BinanceDataCollector.Application.Interfaces;

public interface IDataSyncService
{
    /// <summary>
    /// Запускает долгоживущий процесс сбора данных для одного символа.
    /// Задача завершается, когда приходит сигнал отмены.
    /// </summary>
    /// <param name="symbol">Символ для отслеживания (например, "BTCUSDT").</param>
    /// <param name="cancellationToken">Токен для отмены операции.</param>
    /// <returns>Задача, представляющая жизненный цикл сбора данных.</returns>
    Task StartTradeCollectionAsync(string symbol, CancellationToken cancellationToken);
}