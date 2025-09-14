using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using Npgsql;
using Testcontainers.PostgreSql; // <--- NuGet: Testcontainers.PostgreSql

// Этот класс - "пусковая установка" для вашего приложения в тестах
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // 1. Создаем "чертеж" для Docker-контейнера с PostgreSQL
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    // 2. Метод, который запускается ОДИН РАЗ перед всеми тестами в классе
    public async Task InitializeAsync()
    {
        // Запускаем контейнер в Docker
        await _dbContainer.StartAsync();

        // "Накатываем" нашу схему БД (таблицы, функции) на эту чистую, временную базу
        await ApplyDbSchema();
    }

    // 3. Метод, который запускается ОДИН РАЗ после всех тестов
    public new async Task DisposeAsync()
    {
        // Останавливаем и удаляем контейнер
        await _dbContainer.StopAsync();
    }

    // 4. Главная магия: Переопределяем конфигурацию приложения
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => {
            // Здесь мы "подменяем" строку подключения в DI-контейнере нашего приложения
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // ЗАСТАВЛЯЕМ приложение использовать нашу временную базу, а не ту, что в appsettings.json
                    ["ConnectionStrings:DefaultConnection"] = _dbContainer.GetConnectionString(),
                    ["ConnectionStrings:HangfireConnection"] = _dbContainer.GetConnectionString() // Hangfire тоже будет в этой базе
                })
                .Build());

            // Здесь же можно подменить IBinanceService на мок, как мы обсуждали
        });
    }

    private async Task ApplyDbSchema()
    {
        string schemaSql;
        try
        {
            // 1. Читаем весь DDL-скрипт из файла
            schemaSql = await File.ReadAllTextAsync("schema.sql");
        }
        catch (FileNotFoundException)
        {
            // Обработка случая, если файл не найден
            throw new InvalidOperationException(
                "Не удалось найти файл schema.sql. " +
                "Убедитесь, что он находится в корне тестового проекта и " +
                "для него установлено свойство 'Copy to Output Directory' в 'Copy if newer'.");
        }

        // 2. Убираем "шум", который может мешать выполнению
        // pg_dump может добавлять команды, которые требуют прав суперпользователя,
        // а пользователь в Testcontainers их может не иметь.
        var commands = schemaSql
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(cmd => cmd.Trim())
            // Фильтруем команды, которые могут вызвать ошибки прав доступа
            .Where(cmd =>
                !string.IsNullOrWhiteSpace(cmd) &&
                !cmd.StartsWith("SET ", StringComparison.OrdinalIgnoreCase) &&
                !cmd.StartsWith("SELECT pg_catalog.set_config", StringComparison.OrdinalIgnoreCase) &&
                !cmd.StartsWith("ALTER FUNCTION", StringComparison.OrdinalIgnoreCase) && // Владельца менять не будем
                !cmd.StartsWith("\\connect", StringComparison.OrdinalIgnoreCase)
            );

        // 3. Выполняем скрипт на нашей временной базе
        try
        {
            await using var connection = new NpgsqlConnection(_dbContainer.GetConnectionString());
            await connection.OpenAsync();

            foreach (var commandText in commands)
            {
                await using var command = new NpgsqlCommand(commandText, connection);
                await command.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            // Если что-то пошло не так, выбрасываем подробное исключение
            throw new InvalidOperationException($"Ошибка при применении схемы БД: {ex.Message}", ex);
        }
    }
}