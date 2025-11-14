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
    private readonly string _messageTemplate; // Храним оригинальный шаблон
    private readonly object[] _args; // Храним аргументы
    private readonly Stopwatch _stopwatch;

    public TimedLogOperation(ILogger logger, LogLevel logLevel, string messageTemplate, params object[] args)
    {
        _logger = logger;
        _logLevel = logLevel;
        _messageTemplate = messageTemplate; // Сохраняем шаблон
        _args = args; // Сохраняем аргументы

        // Создаем массив аргументов для лога "Начинаем..."
        var startArgs = new object[args.Length + 1];
        startArgs[0] = messageTemplate; // Первый аргумент - сам шаблон
        Array.Copy(args, 0, startArgs, 1, args.Length);

        // ===== ИСПРАВЛЕНИЕ ЗДЕСЬ =====
        // Передаем шаблон и АРГУМЕНТЫ в него
        _logger.Log(logLevel, "Начинаем операцию: " + messageTemplate, args);
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _stopwatch.Stop();

        // Создаем массив аргументов для лога "Завершено..."
        var endArgs = new object[_args.Length + 2];
        Array.Copy(_args, 0, endArgs, 0, _args.Length);
        endArgs[_args.Length] = _stopwatch.ElapsedMilliseconds;

        // ===== И ИСПРАВЛЕНИЕ ЗДЕСЬ =====
        _logger.Log(_logLevel, "Операция '" + _messageTemplate + "' завершена за {ElapsedMilliseconds} мс.", endArgs);
    }
}
