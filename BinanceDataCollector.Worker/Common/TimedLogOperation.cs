using System.Diagnostics;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Вспомогательный класс для логирования времени выполнения операции.
/// Используется в блоке 'using'.
/// </summary>
public class TimedLogOperation : IDisposable
{
    private readonly ILogger _logger;
    private readonly LogLevel _logLevel;
    private readonly string _message;
    private readonly Stopwatch _stopwatch;

    public TimedLogOperation(ILogger logger, LogLevel logLevel, string message, params object[] args)
    {
        _logger = logger;
        _logLevel = logLevel;
        _logger.Log(logLevel, "Начинаем операцию: {OperationMessage}...", message);
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _logger.Log(_logLevel, "Операция '{OperationMessage}' завершена за {ElapsedMilliseconds} мс.", _message, _stopwatch.ElapsedMilliseconds);
    }
}
