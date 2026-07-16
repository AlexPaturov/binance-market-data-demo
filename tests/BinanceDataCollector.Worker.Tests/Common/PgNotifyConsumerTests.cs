using BinanceDataCollector.Worker.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Worker.Tests.Common;

/// <summary>
/// Цикл потребителя события. Проверяется то, чего не было у таймерной модели: разбор идёт
/// от появления работы, продолжается до дна и переживает сбой отдельного куска.
/// </summary>
public sealed class PgNotifyConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private const string Channel = "test_work";

    /// <summary>Заведомо больше любого теста: с таким тиком потребителя двигает только уведомление.</summary>
    private static readonly TimeSpan NoSafetyTick = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FastSafetyTick = TimeSpan.FromMilliseconds(150);

    private IConfiguration _configuration = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DirectConnection"] = _db.GetConnectionString()
            })
            .Build();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>
    /// Уведомление доходит и будит разбор. Тик страховки здесь заведомо недостижим, поэтому
    /// пройти тест можно только настоящей доставкой `NOTIFY` — если её не будет (например,
    /// соединение слушателя увели через PgBouncer в режиме transaction), тест встанет
    /// по таймауту, а не «просто подождёт подольше».
    /// </summary>
    [Fact]
    public async Task Consumer_StartsWork_WhenNotified()
    {
        var consumer = new CountingConsumer(_configuration, NoSafetyTick, chunks: new[] { 0, 1, 0 });

        await RunAsync(consumer, async () =>
        {
            // Первый разбор на старте уже прошёл и упёрся в пустую очередь.
            await WaitUntilAsync(() => consumer.Calls >= 1);

            await NotifyAsync();

            await WaitUntilAsync(() => consumer.Calls >= 3);
        });
    }

    /// <summary>
    /// Одно уведомление — не один глоток: потребитель пьёт очередь до дна. Иначе хвост
    /// в сотни тысяч минут после импорта архивов расходовался бы по куску на событие.
    /// </summary>
    [Fact]
    public async Task Consumer_DrainsQueueToEmpty_NotOneChunkPerWakeup()
    {
        // Работа, накопившаяся пока сервис не работал, своего уведомления уже не дождётся —
        // поэтому разбор начинается сразу, без события.
        var consumer = new CountingConsumer(_configuration, NoSafetyTick, chunks: new[] { 3, 2, 1, 0 });

        await RunAsync(consumer, async () =>
        {
            // Четвёртый вызов вернул 0 — дно. Значит потребитель шёл, пока работа была.
            await WaitUntilAsync(() => consumer.Calls >= 4);
        });

        Assert.Equal(4, consumer.Calls);
    }

    /// <summary>
    /// Падение одного куска не останавливает потребителя: он вернётся к работе на
    /// страховочной перепроверке. У таймерной модели сбой воркера оставлял флаг Running
    /// поднятым и замораживал расписание до вмешательства сторожа (13.07.2026).
    /// </summary>
    [Fact]
    public async Task Consumer_KeepsRunning_AfterChunkFailure()
    {
        var consumer = new ThrowingOnceConsumer(_configuration, FastSafetyTick);

        await RunAsync(consumer, async () =>
        {
            await WaitUntilAsync(() => consumer.Calls >= 2);
        });

        Assert.True(consumer.Threw, "Первый вызов обязан был упасть.");
    }

    /// <summary>
    /// Потерянный NOTIFY не подвешивает потребителя: страховочная перепроверка поднимает
    /// его сама. Здесь работа появляется в обход уведомления — сигнала не будет вовсе.
    /// </summary>
    [Fact]
    public async Task Consumer_PicksUpWork_WhenNotificationIsLost()
    {
        var consumer = new CountingConsumer(_configuration, FastSafetyTick, chunks: new[] { 0, 0, 1, 0 });

        await RunAsync(consumer, async () =>
        {
            await WaitUntilAsync(() => consumer.Calls >= 4);
        });
    }

    // --- вспомогательное ---

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private async Task NotifyAsync()
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand($"NOTIFY {Channel};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(TestConsumer consumer, Func<Task> scenario)
    {
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            await scenario();
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> until)
    {
        using var cts = new CancellationTokenSource(Timeout);

        while (!until())
        {
            if (cts.IsCancellationRequested)
            {
                Assert.Fail("Условие не выполнилось за отведённое время.");
            }

            await Task.Delay(20);
        }
    }

    private abstract class TestConsumer : PgNotifyConsumer
    {
        private readonly TimeSpan _safetyRecheck;
        private int _calls;

        protected TestConsumer(IConfiguration configuration, TimeSpan safetyRecheck)
            : base(configuration, NullLogger<TestConsumer>.Instance) => _safetyRecheck = safetyRecheck;

        protected override string Channel => PgNotifyConsumerTests.Channel;

        protected override TimeSpan SafetyRecheck => _safetyRecheck;

        // Пауза после обрыва укорочена: тест не может ждать штатные 5 секунд.
        protected override TimeSpan RetryDelay => TimeSpan.FromMilliseconds(50);

        public int Calls => Volatile.Read(ref _calls);

        protected int NextCall() => Interlocked.Increment(ref _calls);
    }

    /// <summary>Отдаёт заранее заданную последовательность «размеров куска».</summary>
    private sealed class CountingConsumer : TestConsumer
    {
        private readonly int[] _chunks;

        public CountingConsumer(IConfiguration configuration, TimeSpan safetyRecheck, int[] chunks)
            : base(configuration, safetyRecheck) => _chunks = chunks;

        protected override Task<int> ProcessChunkAsync(CancellationToken stoppingToken)
        {
            var call = NextCall();

            // За пределами сценария очередь пуста.
            var processed = call <= _chunks.Length ? _chunks[call - 1] : 0;

            return Task.FromResult(processed);
        }
    }

    /// <summary>Падает на первом вызове, дальше работает.</summary>
    private sealed class ThrowingOnceConsumer : TestConsumer
    {
        public bool Threw { get; private set; }

        public ThrowingOnceConsumer(IConfiguration configuration, TimeSpan safetyRecheck)
            : base(configuration, safetyRecheck)
        {
        }

        protected override Task<int> ProcessChunkAsync(CancellationToken stoppingToken)
        {
            if (NextCall() == 1)
            {
                Threw = true;
                throw new InvalidOperationException("Кусок упал.");
            }

            return Task.FromResult(0);
        }
    }
}
