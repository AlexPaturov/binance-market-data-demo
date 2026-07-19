using Hangfire;

namespace BinanceDataCollector.Application.Archives.Interfaces;

public interface IArchiveUnpackerWorker
{
    // Очередь задаётся ЗДЕСЬ, а не только на классе-реализации: DataManager ставит задачу
    // через Enqueue<IArchiveUnpackerWorker>, и Hangfire читает атрибуты с того метода, под
    // которым задача сохранена, — с интерфейсного. Атрибут на классе для таких задач
    // не действует (19.07.2026: 1035 распаковок легли в default вопреки фиксу на классе).
    [Queue("archive_import")]
    Task UnpackArchiveAsync(string zipFileName, string connectionId);
}
