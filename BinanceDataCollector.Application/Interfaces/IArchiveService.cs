using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

public interface IArchiveService
{
    /// <summary>
    /// Скачивает, распаковывает и парсит CSV-архив сделок Binance за указанный день.
    /// </summary>
    /// <returns>Коллекция объектов Trade.</returns>
    IAsyncEnumerable<Trade> DownloadAndParseTradesAsync(string symbol, DateOnly date, CancellationToken cancellationToken);

    /// <summary>
    /// Скачивает архив сделок Binance за указанный день и потоково сохраняет его в предоставленный файловый поток.
    /// </summary>
    /// <param name="symbol">Торговая пара, например, BTCUSDT.</param>
    /// <param name="date">Дата, за которую нужно скачать архив.</param>
    /// <param name="targetStream">Открытый файловый поток (FileStream), куда будут записаны данные.</param>
    /// <param name="cancellationToken">Токен для отмены операции.</param>
    Task DownloadArchiveToStreamAsync(string symbol, DateOnly date, Stream fileStream, CancellationToken none);
}
