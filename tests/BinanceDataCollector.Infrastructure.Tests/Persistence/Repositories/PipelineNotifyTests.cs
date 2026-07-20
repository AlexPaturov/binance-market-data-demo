using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BinanceDataCollector.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// Свойства событийной модели, ради которых она вводилась.
///
/// Разбор был привязан к тику таймера и брал фиксированную пачку в одну транзакцию:
/// не уложилась в командный таймаут — откат всей пачки, и через минуту таймер повторял
/// тот же откат (13–14.07.2026). Здесь проверяется не сам таймаут (600 с на прод-объёме
/// в CI не воспроизвести), а свойство, сделавшее его фатальным: <b>обрыв одного вызова
/// обнулял весь прогресс</b>. Плюс доставка события, на которой держится вся модель.
/// </summary>
public sealed class PipelineNotifyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("market_analytics")
        .Build();

    private TradeRepository _repository = null!;
    private string _connectionString = null!;

    private const string Symbol = "BTCUSDT";
    private const long Jan2026Ms = 1_767_225_600_000;   // 2026-01-01T00:00:00Z

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _connectionString = _db.GetConnectionString();

        var schemaSql = await File.ReadAllTextAsync("02_baseline.sql");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(schemaSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString
            })
            .Build();

        _repository = new TradeRepository(configuration, NullLogger<TradeRepository>.Instance);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>
    /// Вставка тиков будит агрегатор: без этого потребитель живёт только страховочной
    /// перепроверкой раз в минуту — то есть остаётся таймером под другим именем.
    /// </summary>
    [Fact]
    public async Task Insert_NotifiesDirtyMinutes_WhenTicksAreNew()
    {
        await using var listener = await ListenAsync("dirty_minutes");
        var notified = WaitForNotificationAsync(listener);

        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));

        Assert.True(await notified, "Вставка новых тиков обязана разбудить агрегатор.");
    }

    /// <summary>
    /// Повторный импорт того же архива ничего не пачкает — и будить потребителя незачем.
    /// </summary>
    [Fact]
    public async Task Insert_DoesNotNotify_WhenTicksAlreadyExist()
    {
        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));

        await using var listener = await ListenAsync("dirty_minutes");
        var notified = WaitForNotificationAsync(listener);

        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));   // тот же тик ещё раз

        Assert.False(await notified, "Повторная вставка того же тика не создаёт работы.");
    }

    /// <summary>
    /// Агрегация будит расчёт индикаторов: свеча пересчитана — фичи по ней устарели.
    /// </summary>
    [Fact]
    public async Task Aggregation_NotifiesCandlesNew_WhenCandlesWereBuilt()
    {
        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));

        await using var listener = await ListenAsync("candles_new");
        var notified = WaitForNotificationAsync(listener);

        await _repository.AggregateDirtyMinutesAsync(100);

        Assert.True(await notified, "Пересчитанная свеча обязана разбудить расчёт индикаторов.");
    }

    [Fact]
    public async Task Aggregation_DoesNotNotify_WhenQueueIsEmpty()
    {
        await using var listener = await ListenAsync("candles_new");
        var notified = WaitForNotificationAsync(listener);

        await _repository.AggregateDirtyMinutesAsync(100);

        Assert.False(await notified, "Пустая очередь не создаёт работы для индикаторов.");
    }

    /// <summary>
    /// Ключевое свойство модели: вызов — это коммит. Обрыв соединения посреди разбора
    /// оставляет уже закоммиченные куски на месте, а очередь — годной к продолжению.
    ///
    /// Именно это свойство отсутствовало у таймерной модели: пачка целиком в одной
    /// транзакции превращала любой обрыв в потерю всего прохода.
    /// </summary>
    [Fact]
    public async Task Drain_KeepsCommittedChunks_WhenCallIsAbortedMidway()
    {
        // Три минуты работы, разбираем кусками по одной.
        await InsertTradesAsync(
            (1, Minute(0) + 1_000, 100m, 1m),
            (2, Minute(1) + 1_000, 110m, 1m),
            (3, Minute(2) + 1_000, 120m, 1m));

        Assert.Equal(1, await _repository.AggregateDirtyMinutesAsync(1));
        Assert.Equal(1, await _repository.AggregateDirtyMinutesAsync(1));

        // Обрыв посреди разбора: соединение убивают со стороны сервера.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var doomed = new NpgsqlConnection(_connectionString);
            await doomed.OpenAsync();

            var pid = await doomed.ExecuteScalarAsync<int>("SELECT pg_backend_pid();");

            await using var killer = new NpgsqlConnection(_connectionString);
            await killer.OpenAsync();
            await killer.ExecuteAsync("SELECT pg_terminate_backend(@Pid);", new { Pid = pid });

            await doomed.ExecuteScalarAsync<int>("SELECT public.sp_aggregate_dirty_minutes(1);");
        });

        // Закоммиченное на месте: две свечи из двух успевших вызовов.
        Assert.Equal(2, await CandleCountAsync());

        // Очередь цела, оставшаяся минута никуда не делась — разбор продолжается с неё.
        Assert.Equal(1, await DirtyMinuteCountAsync());
        Assert.Equal(1, await _repository.AggregateDirtyMinutesAsync(100));
        Assert.Equal(0, await DirtyMinuteCountAsync());
        Assert.Equal(3, await CandleCountAsync());
    }

    /// <summary>
    /// Потребитель пьёт очередь до дна, а не делает один глоток на событие. Признак дна —
    /// ноль снятых минут: минута без тиков свечи не даёт, но с очереди уходит, и по числу
    /// свечей разбор останавливался бы на ней, не дойдя до конца.
    /// </summary>
    [Fact]
    public async Task Drain_ReportsMinutesTaken_NotCandlesBuilt()
    {
        await InsertTradesAsync((1, Minute(0) + 1_000, 100m, 1m));

        // Тики минуты удалены (так делает ротация партиций), а минута осталась в очереди.
        await ExecuteAsync(@"DELETE FROM public.""Trades"";");

        // Минута снята с очереди — работа сделана, хотя свечи из неё не вышло.
        Assert.Equal(1, await _repository.AggregateDirtyMinutesAsync(100));
        Assert.Equal(0, await CandleCountAsync());
        Assert.Equal(0, await DirtyMinuteCountAsync());

        // А вот теперь очередь действительно пуста — это и есть дно.
        Assert.Equal(0, await _repository.AggregateDirtyMinutesAsync(100));
    }

    // --- вспомогательное ---

    private static long Minute(int offset) => Jan2026Ms + offset * 60_000L;

    private async Task<NpgsqlConnection> ListenAsync(string channel)
    {
        var listener = new NpgsqlConnection(_connectionString);
        await listener.OpenAsync();
        await listener.ExecuteAsync($"LISTEN {channel};");

        return listener;
    }

    /// <summary>
    /// Ждёт уведомление ограниченное время. `false` — не пришло: отсутствие сигнала
    /// иначе не проверить, ждать «вечно» тест не может.
    /// </summary>
    private static async Task<bool> WaitForNotificationAsync(NpgsqlConnection listener)
    {
        var received = false;
        listener.Notification += (_, _) => received = true;

        await listener.WaitAsync(TimeSpan.FromSeconds(2));

        return received;
    }

    private async Task InsertTradesAsync(params (long Id, long Time, decimal Price, decimal Qty)[] trades)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            @"SELECT public.sp_bulk_insert_trades(
                  @Ids, @Symbols, @Prices, @Quantities, @QuoteQuantities,
                  @Times, @IsBuyerMakers, @IsBestMatches);",
            new
            {
                Ids = trades.Select(t => t.Id).ToArray(),
                Symbols = trades.Select(_ => Symbol).ToArray(),
                Prices = trades.Select(t => t.Price).ToArray(),
                Quantities = trades.Select(t => t.Qty).ToArray(),
                QuoteQuantities = trades.Select(t => t.Price * t.Qty).ToArray(),
                Times = trades.Select(t => t.Time).ToArray(),
                IsBuyerMakers = trades.Select(_ => false).ToArray(),
                IsBestMatches = trades.Select(_ => true).ToArray()
            });
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private async Task<int> CandleCountAsync() =>
        await QuerySingleAsync<int>(@"SELECT count(*)::int FROM public.""Ohlcv_1min"";");

    private async Task<int> DirtyMinuteCountAsync() =>
        await QuerySingleAsync<int>(@"SELECT count(*)::int FROM public.""DirtyMinutes"";");

    private async Task<T> QuerySingleAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<T>(sql);
    }
}
