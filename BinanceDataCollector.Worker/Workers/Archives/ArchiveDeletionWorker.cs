using BinanceDataCollector.Domain.DTOs;
using Hangfire;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Worker.Workers.Archives;

[Queue("archive_import")] // Можно выполнять в той же низкоприоритетной очереди
public class ArchiveDeletionWorker
{
    private readonly ILogger<ArchiveDeletionWorker> _logger;
    private readonly string _archivesPath;

    public ArchiveDeletionWorker(ILogger<ArchiveDeletionWorker> logger, IOptions<ArchivesSettings> options)
    {
        _logger = logger;
        _archivesPath = options.Value.TradeArcihvesPath;
    }

    // Hangfire будет вызывать этот метод
    public Task DeleteFileAsync(string fileName)
    {
        var filePath = Path.Combine(_archivesPath, fileName);
        _logger.LogInformation("Начинаю удаление файла: {FileName}", fileName);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Файл {FileName} успешно удален.", fileName);
            }
            else
            {
                _logger.LogWarning("Файл {FileName} не найден для удаления.", fileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при удалении файла {FileName}", fileName);
            // Перевыбрасываем, чтобы Hangfire пометил задачу как Failed
            throw;
        }

        return Task.CompletedTask;
    }
}
