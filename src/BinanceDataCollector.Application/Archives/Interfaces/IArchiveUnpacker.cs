namespace BinanceDataCollector.Application.Archives.Interfaces;

public interface IArchiveUnpacker
{
    Task UnpackArchiveAsync(string zipFileName, string connectionId);
    
}