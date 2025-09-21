using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection; // <-- Важный using
using Microsoft.Extensions.Hosting;
namespace BinanceDataCollector.Worker.Common;

// 1. Создаем свой собственный, пустой фильтр авторизации, чтобы сделать дашборд публичным.
// Это официальный способ, рекомендуемый документацией Hangfire.
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}

public class DashboardHostedService : IHostedService
{
    private readonly IWebHost _webHost;
    // Принимаем IServiceProvider, чтобы иметь доступ к основному DI-контейнеру
    public DashboardHostedService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        // 1. Создаем JobStorage СПЕЦИАЛЬНО для дашборда,
        // используя ту же строку подключения, что и основной сервер.
        var storage = new PostgreSqlStorage(
            configuration.GetConnectionString("HangfireConnection")
        );

        _webHost = new WebHostBuilder()
            .UseKestrel()
            .UseUrls("http://*:7001")

            // 2. Настраиваем DI-контейнер для "микро-веб-хоста".
            .ConfigureServices(services => {
                // 3. ЯВНО "пробрасываем" необходимые сервисы Hangfire из
                // основного контейнера в дочерний.
                services.AddSingleton(serviceProvider.GetRequiredService<JobStorage>());
                // Добавляем поддержку самого Hangfire
                services.AddHangfire(config => { });
            })

            .Configure(app => {
                // 4. Теперь `UseHangfireDashboard` найдет все, что ему нужно,
                // в своем собственном, правильно настроенном DI-контейнере.
                app.UseHangfireDashboard("/hangfire", new DashboardOptions
                {
                    // Используем наш собственный фильтр для авторизации
                    Authorization = new[] { new HangfireAuthorizationFilter() }
                });
            })
            .Build();
    }


    public Task StartAsync(CancellationToken cancellationToken) => _webHost.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _webHost.StopAsync(cancellationToken);
}
