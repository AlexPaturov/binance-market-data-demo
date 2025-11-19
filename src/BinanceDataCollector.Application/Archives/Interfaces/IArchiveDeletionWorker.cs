namespace BinanceDataCollector.Application.Archives.Interfaces;

public interface IArchiveDeletionWorker
{
    Task DeleteFileAsync(string fileName);
}