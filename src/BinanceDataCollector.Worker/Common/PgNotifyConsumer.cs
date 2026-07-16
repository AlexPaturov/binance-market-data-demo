using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Постоянный потребитель события Postgres: ждёт `NOTIFY` по каналу и разбирает очередь досуха.
///
/// Заменяет собой периодическую задачу. Обработка, привязанная к тику таймера, разбирала
/// фиксированную пачку в одной транзакции: не уложилась в командный таймаут — откат всей
/// пачки, и через минуту таймер запускал ровно тот же откат (13–14.07.2026, агрегация свечей).
/// Здесь работа идёт кусками, <b>каждый кусок — свой вызов, своя транзакция и свой коммит</b>:
/// обрыв посреди разбора оставляет уже закоммиченные куски на месте, а не обнуляет проход.
///
/// Ждём событие, а не время: свеча появляется вслед за тиками, а не на ближайшей границе минуты.
/// </summary>
/// <remarks>
/// Соединение слушателя идёт <b>напрямую в Postgres, мимо PgBouncer</b> (строка подключения
/// `DirectConnection`). PgBouncer работает в режиме transaction и возвращает серверное
/// соединение в пул после каждой транзакции — регистрация `LISTEN` через него не переживает
/// транзакцию, и уведомления до слушателя не доходят. Рабочие вызовы (разбор очереди) идут
/// через пул как раньше: им транзакционный режим не мешает.
///
/// Строка обязательна: без неё сервис падает на старте. Молчаливый откат на пул дал бы
/// работающий на вид конвейер, который на деле опрашивает базу раз в минуту — тот же таймер
/// под другим именем.
/// </remarks>
public abstract class PgNotifyConsumer : BackgroundService
{
    /// <summary>
    /// Страховка от потерянного уведомления: сигнал мог пропасть, пока слушатель
    /// переподключался. Разбор всё равно начнётся, самое позднее — через этот срок.
    /// </summary>
    protected virtual TimeSpan SafetyRecheck => TimeSpan.FromSeconds(60);

    /// <summary>Пауза перед переподключением слушателя.</summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly ILogger _logger;

    /// <summary>Канал Postgres. Только константы: в `LISTEN` имя канала подставляется в текст запроса.</summary>
    protected abstract string Channel { get; }

    protected PgNotifyConsumer(IConfiguration configuration, ILogger logger)
    {
        _connectionString = configuration.GetConnectionString("DirectConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DirectConnection' not found. LISTEN/NOTIFY requires a direct " +
                "connection to Postgres: PgBouncer in transaction mode does not carry listeners.");
        _logger = logger;
    }

    /// <summary>
    /// Разбирает один кусок работы в отдельной транзакции. Возвращает сколько обработано;
    /// 0 — очередь пуста.
    /// </summary>
    protected abstract Task<int> ProcessChunkAsync(CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAndDrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Обрыв соединения слушателя. Работа не потеряна: она лежит в очереди,
                // её заберёт следующее подключение.
                _logger.LogError(ex, "Listener on channel {Channel} dropped. Reconnecting.", Channel);

                if (!await DelayAsync(RetryDelay, stoppingToken)) break;
            }
        }

        _logger.LogInformation("Consumer of channel {Channel} stopped.", Channel);
    }

    private async Task ListenAndDrainAsync(CancellationToken stoppingToken)
    {
        await using var listener = new NpgsqlConnection(_connectionString);
        await listener.OpenAsync(stoppingToken);

        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken);
        }

        _logger.LogInformation("Listening on channel {Channel}.", Channel);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Сначала разбор, потом ожидание: на старте в очереди уже может лежать работа,
            // накопленная пока сервис не работал, и своего уведомления она не дождётся.
            await DrainAsync(stoppingToken);

            await listener.WaitAsync(SafetyRecheck, stoppingToken);
        }
    }

    /// <summary>Пьёт очередь до дна: один глоток не заканчивает работу, если в очереди ещё есть.</summary>
    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await ProcessChunkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Один упавший кусок не останавливает потребителя: закоммиченное на месте,
                // упавший кусок остался в очереди, к нему вернёмся на страховочной перепроверке.
                _logger.LogError(ex, "Chunk on channel {Channel} failed. Queue is kept, retrying later.", Channel);
                return;
            }

            if (processed == 0) return;
        }
    }

    /// <summary>Пауза, устойчивая к остановке сервиса. `false` — пора выходить.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
