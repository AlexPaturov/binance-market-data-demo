using BinanceDataCollector.Application.Archives.Models;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Archives.Interfaces;

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
    Task<bool> DownloadArchiveToStreamAsync(string symbol, DateOnly date, Stream fileStream, CancellationToken none);

    /// <summary>
    /// Получает список информации о скачанных файлах архивов из файловой системы.
    /// </summary>
    /// <returns>Список объектов ArchivedFileInfo.</returns>
    Task<List<ArchivedFileInfo>> GetArchivedFilesAsync();

    /// <summary>
    /// Открывает локальный ZIP-архив, находит CSV и парсит его. 
    /// </summary>
    /// <param name="zipFilePath"></param>
    /// <param name="symbol"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    IAsyncEnumerable<Trade> ParseTradesFromLocalZipAsync(string zipFilePath, string symbol, CancellationToken cancellationToken);

    IAsyncEnumerable<Trade> ParseTradesFromCsvStreamAsync(Stream csvStream, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Распаковывает локальный ZIP-архив в памяти и возвращает его содержимое.
    /// </summary>
    /// <param name="zipFileName"></param>
    /// <param name="pageNumber"></param>
    /// <param name="pageSize"></param>
    /// <returns>Список объектов Trade.</returns>
    Task<InspectArchiveContentResult> InspectArchiveContentAsync(string zipFileName, int pageNumber, int pageSize);
}
