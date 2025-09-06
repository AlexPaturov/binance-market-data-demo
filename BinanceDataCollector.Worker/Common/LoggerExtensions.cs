using Serilog.Context;
using System.Runtime.CompilerServices;

namespace BinanceDataCollector.Worker.Common;

public static class LoggerExtensions
{
    public static IDisposable TimedOperation(this ILogger logger, LogLevel level, string message, params object[] args)
    {
        return new TimedLogOperation(logger, level, message, args);
    }

    public static IDisposable TimedOperation(this ILogger logger, string message, params object[] args)
    {
        return new TimedLogOperation(logger, LogLevel.Information, message, args);
    }

    // каверкает строку, портит красивое форматирование в консоли 
    public static void LogInfoWithCaller(this ILogger logger, string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        using var memberProp = LogContext.PushProperty("MemberName", memberName);
        using var fileProp = LogContext.PushProperty("FilePath", sourceFilePath);
        using var lineProp = LogContext.PushProperty("LineNumber", sourceLineNumber);

        logger.LogInformation(message);
    }
}
