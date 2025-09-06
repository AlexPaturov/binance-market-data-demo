using System.Runtime.CompilerServices;

namespace BinanceDataCollector.Worker.Common;

public static class LoggerNames
{
    public static string GetCurrentMethodName([CallerMemberName] string methodName = "")
    {
        return methodName;
    }
}
