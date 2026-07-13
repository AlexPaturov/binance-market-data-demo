using Binance.Net.SymbolOrderBooks;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects;
using System.Collections.Concurrent;

namespace BinanceDataCollector.Worker.Workers;

/// <summary>
/// Собирает фичи стакана по активным парам.
///
/// Сырой L2 не хранится: полная глубина с диффами по 40 парам — это ~190 ГБ/месяц даже
/// в экономной схеме, что съело бы бюджет, отведённый под тики. Книга держится в памяти,
/// раз в 5 секунд с неё снимается срез, и в конце минуты усреднённые фичи (дисбаланс,
/// глубина у цены, спред, стенки, скорость обновления) пишутся одной строкой на пару.
///
/// Протокол «снапшот + диффы + проверка непрерывности + ресинк при разрыве» не пишется
/// руками — его реализует <see cref="BinanceSpotSymbolOrderBook"/> из Binance.Net.
/// Пока книга не в статусе Synced, срезы не снимаются: неполная книга дала бы неверные
/// фичи, а это хуже, чем их отсутствие.
///
/// Истории у этих данных нет и быть не может: архивов глубины по споту Binance не
/// публикует. Всё, что соберётся — соберётся с момента запуска.
/// </summary>
public class OrderBookCollectorWorker : BackgroundService
{
    /// <summary>Как часто снимается срез книги. Одиночный снимок за минуту слишком шумный.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Пауза между стартами книг. Каждая при запуске тянет REST-снапшот; если запустить
    /// 40 пар разом, Binance ответит баном по rate limit.
    /// </summary>
    private static readonly TimeSpan StartupStagger = TimeSpan.FromMilliseconds(300);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrderBookFeatureCalculator _calculator;
    private readonly ILogger<OrderBookCollectorWorker> _logger;

    private readonly List<ISymbolOrderBook> _books = new();
    private readonly ConcurrentDictionary<string, MinuteAccumulator> _accumulators = new();

    public OrderBookCollectorWorker(
        IServiceScopeFactory scopeFactory,
        IOrderBookFeatureCalculator calculator,
        ILogger<OrderBookCollectorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _calculator = calculator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Order book collector started.");

        var symbols = await WaitForSymbolsAsync(stoppingToken);
        if (symbols.Count == 0) return;

        await StartBooksAsync(symbols, stoppingToken);

        var timer = new PeriodicTimer(SampleInterval);
        var currentMinute = CurrentMinute();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var minute = CurrentMinute();

                // Минута закрылась — усредняем накопленное и пишем.
                if (minute != currentMinute)
                {
                    await FlushMinuteAsync(currentMinute);
                    currentMinute = minute;
                }

                SampleBooks();
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка: последнюю минуту всё равно сохраняем.
            await FlushMinuteAsync(currentMinute);
        }
        finally
        {
            foreach (var book in _books)
            {
                await book.StopAsync();
            }

            _logger.LogInformation("Order book collector stopped.");
        }
    }

    private async Task<List<string>> WaitForSymbolsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var symbolRepository = scope.ServiceProvider.GetRequiredService<ITrackedSymbolRepository>();
            var symbols = (await symbolRepository.GetActiveSymbolsAsync()).ToList();

            if (symbols.Count > 0) return symbols;

            _logger.LogWarning("No active symbols for order book collection. Retrying in 5 minutes.");
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        return new List<string>();
    }

    private async Task StartBooksAsync(List<string> symbols, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting order books for {Count} symbols...", symbols.Count);

        foreach (var symbol in symbols)
        {
            if (stoppingToken.IsCancellationRequested) return;

            var book = new BinanceSpotSymbolOrderBook(symbol);
            var accumulator = _accumulators.GetOrAdd(symbol, _ => new MinuteAccumulator());

            book.OnOrderBookUpdate += _ => accumulator.CountUpdate();

            var result = await book.StartAsync(stoppingToken);
            if (!result.Success)
            {
                _logger.LogError("Failed to start order book for {Symbol}: {Error}", symbol, result.Error);
                continue;
            }

            _books.Add(book);

            // Разносим REST-снапшоты во времени, чтобы не упереться в rate limit.
            await Task.Delay(StartupStagger, stoppingToken);
        }

        _logger.LogInformation("Order books running: {Count}.", _books.Count);
    }

    private void SampleBooks()
    {
        foreach (var book in _books)
        {
            // Книга в процессе синхронизации или переподключения — данные неполные.
            // Считать по ней фичи хуже, чем не считать вовсе.
            if (book.Status != OrderBookStatus.Synced) continue;

            var bids = book.Bids.Select(b => new OrderBookLevel(b.Price, b.Quantity)).ToList();
            var asks = book.Asks.Select(a => new OrderBookLevel(a.Price, a.Quantity)).ToList();

            var snapshot = _calculator.Calculate(bids, asks);
            if (snapshot is null) continue;

            if (_accumulators.TryGetValue(book.Symbol, out var accumulator))
            {
                accumulator.Add(snapshot);
            }
        }
    }

    private async Task FlushMinuteAsync(long openTime)
    {
        var features = new List<OrderBookFeature>();

        foreach (var (symbol, accumulator) in _accumulators)
        {
            var (samples, updates) = accumulator.Take();
            if (samples.Count == 0) continue;

            features.Add(_calculator.Aggregate(symbol, openTime, samples, updates));
        }

        if (features.Count == 0)
        {
            _logger.LogWarning("No order book samples for minute {Minute:yyyy-MM-dd HH:mm} — books not synced?",
                DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IOrderBookFeatureRepository>();
            await repository.BulkUpsertAsync(features);

            _logger.LogInformation("Saved order book features for {Count} symbols, minute {Minute:HH:mm}.",
                features.Count, DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save order book features for {Count} symbols.", features.Count);
        }
    }

    private static long CurrentMinute() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000L * 60_000L;

    /// <summary>Копит срезы книги и число её обновлений в пределах одной минуты.</summary>
    private sealed class MinuteAccumulator
    {
        private readonly object _lock = new();
        private List<OrderBookSnapshot> _samples = new();
        private int _updates;

        public void Add(OrderBookSnapshot snapshot)
        {
            lock (_lock) _samples.Add(snapshot);
        }

        public void CountUpdate() => Interlocked.Increment(ref _updates);

        /// <summary>Забирает накопленное и обнуляет счётчики — минута закрылась.</summary>
        public (List<OrderBookSnapshot> Samples, int Updates) Take()
        {
            lock (_lock)
            {
                var samples = _samples;
                var updates = Interlocked.Exchange(ref _updates, 0);
                _samples = new List<OrderBookSnapshot>();
                return (samples, updates);
            }
        }
    }
}
