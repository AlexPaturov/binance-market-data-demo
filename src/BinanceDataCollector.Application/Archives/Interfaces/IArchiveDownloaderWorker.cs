namespace BinanceDataCollector.Application.Archives.Interfaces;

// Этот интерфейс описывает "контракт" для задачи скачивания
public interface IArchiveDownloaderWorker
{
    Task DownloadArchiveAsync(Guid requestId, string connectionId, string symbol, DateOnly date);
}