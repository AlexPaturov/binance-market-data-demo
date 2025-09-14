using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IArchiveService
{
    /// <summary>
    /// Скачивает, распаковывает и парсит CSV-архив сделок Binance за указанный день.
    /// </summary>
    /// <returns>Коллекция объектов Trade.</returns>
    Task<List<Trade>> DownloadAndParseTradesAsync(string symbol, DateOnly date, CancellationToken cancellationToken);
}
