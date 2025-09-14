using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Entities;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Threading.Tasks;
using Xunit;

public class GapFillingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly string _connectionString; // Строка подключения к ТЕСТОВОЙ БД
    private readonly ILogger<GapFillingTests> _logger;

    public GapFillingTests(CustomWebApplicationFactory factory, ILogger<GapFillingTests> logger)
    {
        _factory = factory;
        // Получаем строку подключения к тестовой БД, которую поднял Testcontainers
        _connectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
        _logger = logger;
    }

    // Вспомогательный метод для очистки БД
    private async Task ResetDatabase()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(@"TRUNCATE TABLE ""Trades"", ""HistoricalAudit_Watermarks"" RESTART IDENTITY;");
    }

    [Fact]
    public async Task HistoricalAuditor_ShouldFindAndFill_SingleTradeIdGap()
    {
        // =================================================================
        // ЭТАП 1: ПОДГОТОВКА. Создаем "дыру" в базе.
        // =================================================================
        await ResetDatabase();
        _logger.LogInformation("ЭТАП 1: Создаем искусственную дыру в базе данных...");

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            // Вставляем данные ДО дыры
            await conn.ExecuteAsync(@"INSERT INTO ""Trades"" (...) VALUES (..., 1000, ...)");
            await conn.ExecuteAsync(@"INSERT INTO ""Trades"" (...) VALUES (..., 1001, ...)");

            // Вставляем данные ПОСЛЕ дыры. Пропускаем ID 1002.
            await conn.ExecuteAsync(@"INSERT INTO ""Trades"" (...) VALUES (..., 1003, ...)");

            // Создаем вотермарку, чтобы аудитор начал проверку с самого начала
            await conn.ExecuteAsync(@"INSERT INTO ""HistoricalAudit_Watermarks"" (...) VALUES ('BTCUSDT', 0, ...)");
        }

        // =================================================================
        // ЭТАП 2: ПРОВЕРКА. Убеждаемся, что дыра действительно существует.
        // =================================================================
        _logger.LogInformation("ЭТАП 2: Проверяем, что дыра существует...");

        List<DataGap> gapsBefore;
        // Используем ваш рабочий репозиторий для поиска дыр
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var tradesRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            gapsBefore = await tradesRepo.GetGapsForSymbolDayAsync("BTCUSDT"); // Предполагаем, что сделки в пределах 48 часов
        }

        gapsBefore.Should().HaveCount(1);
        gapsBefore.First().GapStart.Should().Be(1001);
        gapsBefore.First().GapEnd.Should().Be(1003);

        // =================================================================
        // ЭТАП 3: ЗАПУСК ПРИЛОЖЕНИЯ. Даем ему поработать и "вылечить" дыру.
        // =================================================================
        _logger.LogInformation("ЭТАП 3: Запускаем приложение и ждем, пока аудитор отработает...");

        // 1. Получаем IHostApplicationLifetime из DI контейнера фабрики.
        // Этот сервис позволяет управлять жизненным циклом хоста.
        var lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();

        // 2. Создаем CancellationToken, который сработает через 15 секунд.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 3. Подписываемся на событие "Приложение остановлено".
        // Когда сработает наш токен, он остановит хост, и этот Task завершится.
        var waitForStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStopped.Register(() => waitForStop.SetResult());

        // =================================================================
        // ЭТАП 4: ФИНАЛЬНАЯ ПРОВЕРКА. Убеждаемся, что дыра заполнена.
        // =================================================================
        _logger.LogInformation("ЭТАП 4: Проверяем, что дыра была заполнена...");

        List<DataGap> gapsAfter;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var tradesRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            gapsAfter = await tradesRepo.GetGapsForSymbolDayAsync("BTCUSDT");
        }

        // Проверяем, что дыр больше нет!
        gapsAfter.Should().BeEmpty();

        // (Опционально) Можно сделать прямой запрос и убедиться, что сделка с ID 1002 теперь есть в базе.
        int tradeCount;
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            tradeCount = await conn.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""Trades"" WHERE ""TradeId"" = 1002 AND ""Symbol"" = 'BTCUSDT'");
        }
        tradeCount.Should().Be(1);
    }
}