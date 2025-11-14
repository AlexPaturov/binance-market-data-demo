using System.Diagnostics;

namespace BinanceDataCollector.DataManager.Middleware;

public class MemoryUsageLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MemoryUsageLoggingMiddleware> _logger;

    public MemoryUsageLoggingMiddleware(RequestDelegate next, ILogger<MemoryUsageLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }
    
    public async Task InvokeAsync(HttpContext context)
    {
        // --- ДО ВЫПОЛНЕНИЯ ЗАПРОСА ---
        var process = Process.GetCurrentProcess();
        var memoryBefore = process.WorkingSet64; // Память, используемая процессом

        var stopwatch = Stopwatch.StartNew();

        // --- ВЫПОЛНЯЕМ СЛЕДУЮЩИЙ КОМПОНЕНТ ПАЙПЛАЙНА (например, наш контроллер) ---
        await _next(context);

        // --- ПОСЛЕ ВЫПОЛНЕНИЯ ЗАПРОСА ---
        stopwatch.Stop();
        process.Refresh(); // Обновляем информацию о процессе
        var memoryAfter = process.WorkingSet64;

        var memoryUsed = memoryAfter - memoryBefore;

        // Логируем, только если изменение было значительным (например, больше 10 МБ)
        if (Math.Abs(memoryUsed) > 10 * 1024 * 1024) 
        {
            _logger.LogWarning(
                "Memory Usage Change for request {Path}: [Before: {Before:N0} KB] -> [After: {After:N0} KB] -> [Delta: {Delta:N0} KB] in {Elapsed} ms",
                context.Request.Path,
                memoryBefore / 1024,
                memoryAfter / 1024,
                memoryUsed / 1024,
                stopwatch.ElapsedMilliseconds);
        }
        else // Иначе логируем на уровне Debug, чтобы не засорять консоль
        {
            _logger.LogDebug(
                "Memory Usage Change for request {Path}: [Delta: {Delta:N0} KB] in {Elapsed} ms",
                context.Request.Path,
                memoryUsed / 1024,
                stopwatch.ElapsedMilliseconds);
        }
    }
}