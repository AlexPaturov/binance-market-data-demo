namespace BinanceDataCollector.Application.Archives.Interfaces;

public interface IArchiveUnpackerWorker
{
    Task UnpackArchiveAsync(string zipFileName, string connectionId);
    
}