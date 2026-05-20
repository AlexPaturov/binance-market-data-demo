using BinanceDataCollector.Application.Archives.Interfaces;
using Hangfire;

namespace BinanceDataCollector.Worker.Workers.Archives;

[Queue("archive_import")] // Можно выполнять в той же низкоприоритетной очереди
public class ArchiveDeletionWorker
{
    private readonly ILogger<ArchiveDeletionWorker> _logger;
    private readonly string _archivesPath;

    public ArchiveDeletionWorker(ILogger<ArchiveDeletionWorker> logger, IPathProvider pathProvider)
    {
        _logger = logger;
        _archivesPath = pathProvider.GetTradeArchivesPath();
    }

    // Hangfire будет вызывать этот метод
    public Task DeleteFileAsync(string fileName)
    {
        var filePath = Path.Combine(_archivesPath, fileName);
        _logger.LogInformation("Starting deletion of file: {FileName}", fileName);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("File {FileName} deleted successfully.", fileName);
            }
            else
            {
                _logger.LogWarning("File {FileName} not found for deletion.", fileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file {FileName}", fileName);
            // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            throw;
        }

        return Task.CompletedTask;
    }
}
