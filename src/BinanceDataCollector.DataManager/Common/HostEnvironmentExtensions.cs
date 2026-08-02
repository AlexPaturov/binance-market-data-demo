namespace BinanceDataCollector.DataManager.Common;

/// <summary>
/// Demo — самостоятельное окружение наравне с Development/Production. Заводит `IsDemo()`
/// в том же стиле, что встроенные `IsDevelopment()`/`IsProduction()` (обёртки над
/// `IsEnvironment`), чтобы demo-развилки в коде читались явно, а не через строки или
/// косвенные признаки. Активируется `ASPNETCORE_ENVIRONMENT=Demo`.
/// </summary>
public static class HostEnvironmentExtensions
{
    public const string Demo = "Demo";

    public static bool IsDemo(this IHostEnvironment environment) =>
        environment.IsEnvironment(Demo);
}
