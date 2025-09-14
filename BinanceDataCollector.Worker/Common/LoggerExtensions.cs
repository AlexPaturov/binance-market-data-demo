namespace BinanceDataCollector.Worker.Common;

public static class LoggerExtensions
{
    public static IDisposable TimedOperation<T>(this ILogger<T> logger, LogLevel level, string message, params object[] args)
    {
        return new TimedLogOperation(logger, level, message, args);
    }

    public static IDisposable TimedOperation<T>(this ILogger<T> logger, string message, params object[] args)
    {
        return new TimedLogOperation(logger, LogLevel.Information, message, args);
    }
}
