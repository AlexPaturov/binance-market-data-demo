using BinanceDataCollector.Application.Common; // <-- Укажите правильное пространство имен для вашего диспетчера и enum
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Infrastructure.Tests.Common;

public class BinanceApiDispatcherTests
{
    private readonly ILogger<BinanceApiDispatcher> _logger;

    public BinanceApiDispatcherTests()
    {
        // Настраиваем логгер, чтобы видеть, что происходит внутри диспетчера во время теста
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Trace)); // Включаем Trace для детальности
        _logger = loggerFactory.CreateLogger<BinanceApiDispatcher>();
    }

    [Fact]
    public async Task AcquireAccessAsync_WhenCalledConcurrently_ShouldNotDeadlockAndPrioritizeCorrectly()
    {
        // --- ARRANGE ---
        var dispatcher = new BinanceApiDispatcher(_logger);
        var executionOrder = new List<ApiRequestPriority>();
        var lockObject = new object();
        var tasks = new List<Task>();

        // Создаем "стартовые пистолеты" для синхронизации
        var historicalCanStart = new TaskCompletionSource();
        var quickAuditCanStart = new TaskCompletionSource();
        var realtimeCanStart = new TaskCompletionSource();

        // 1. Подготавливаем задачи, но они будут ждать "выстрела"

        // Низкий приоритет
        tasks.Add(Task.Run(async () =>
        {
            await historicalCanStart.Task; // Ждем сигнала
            using (await dispatcher.AquireAccessAsync(ApiRequestPriority.HistoricalAudit, CancellationToken.None))
            {
                await Task.Delay(20);
                lock (lockObject) { executionOrder.Add(ApiRequestPriority.HistoricalAudit); }
            }
        }));

        // Средний приоритет
        tasks.Add(Task.Run(async () =>
        {
            await quickAuditCanStart.Task; // Ждем сигнала
            using (await dispatcher.AquireAccessAsync(ApiRequestPriority.QuickAudit, CancellationToken.None))
            {
                await Task.Delay(10);
                lock (lockObject) { executionOrder.Add(ApiRequestPriority.QuickAudit); }
            }
        }));

        // Высокий приоритет
        tasks.Add(Task.Run(async () =>
        {
            await realtimeCanStart.Task; // Ждем сигнала
            using (await dispatcher.AquireAccessAsync(ApiRequestPriority.Realtime, CancellationToken.None))
            {
                await Task.Delay(5);
                lock (lockObject) { executionOrder.Add(ApiRequestPriority.Realtime); }
            }
        }));

        // --- ACT ---

        // Теперь мы контролируем порядок, в котором задачи пытаются захватить ресурс.
        // "Спускаем с цепи" их в порядке от НИЗКОГО к ВЫСОКОМУ приоритету.
        historicalCanStart.SetResult();
        await Task.Delay(5); // Убеждаемся, что она встала в очередь

        quickAuditCanStart.SetResult();
        await Task.Delay(5); // Убеждаемся, что она встала в очередь

        realtimeCanStart.SetResult(); // Эта должна "обогнать" всех

        // Ждем завершения
        await Task.WhenAll(tasks);

        // --- ASSERT ---
        executionOrder.Count.Should().Be(3);

        // Проверяем, что несмотря на порядок "запуска", они выполнились в порядке ПРИОРИТЕТА
        executionOrder[0].Should().Be(ApiRequestPriority.Realtime);
        executionOrder[1].Should().Be(ApiRequestPriority.QuickAudit);
        executionOrder[2].Should().Be(ApiRequestPriority.HistoricalAudit);
    }

    [Fact]
    public async Task AcquireAccessAsync_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        // --- ARRANGE ---
        var dispatcher = new BinanceApiDispatcher(_logger);
        var cts = new CancellationTokenSource();

        // Запускаем низкоприоритетную задачу, которая "захватит" доступ
        var blockingTask = Task.Run(async () => {
            using (await dispatcher.AquireAccessAsync(ApiRequestPriority.HistoricalAudit, CancellationToken.None))
            {
                // Держим "замок" 100 мс
                await Task.Delay(100);
            }
        });

        // Даем ей время захватить ресурс
        await Task.Delay(10);

        // --- ACT ---
        // Пытаемся получить доступ, но сразу же отменяем операцию
        var action = async () => {
            using (await dispatcher.AquireAccessAsync(ApiRequestPriority.Realtime, cts.Token))
            {
                // Мы не должны сюда попасть
            }
        };

        cts.Cancel(); // Отменяем!

        // --- ASSERT ---
        // Проверяем, что вызов `action` выбросил исключение отмены.
        await action.Should().ThrowAsync<OperationCanceledException>();

        // Ждем, пока блокирующая задача завершится, чтобы тест был "чистым"
        await blockingTask;
    }
}