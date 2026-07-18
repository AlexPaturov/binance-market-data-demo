using System.Diagnostics;
using System.Net;
using BinanceDataCollector.Application.Analytics;
using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Common;
using BinanceDataCollector.DataManager.Common.Auth;
using BinanceDataCollector.DataManager.Hubs;
using BinanceDataCollector.DataManager.Messaging;
using BinanceDataCollector.DataManager.Middleware;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Infrastructure.Messaging;
using BinanceDataCollector.Infrastructure.Persistence.Repositories;
using BinanceDataCollector.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;
using Serilog.Enrichers.WithCaller;

namespace BinanceDataCollector.DataManager;

public class Program {
    public static void Main(string[] args) {
        var startupStopwatch = Stopwatch.StartNew();
        
        #region Минимальный "загрузочный" логгер - записать в консоль ошибку, если .Build() упадет.
        // --- ФАЗА 1: ЗАГРУЗОЧНЫЙ ЛОГГЕР ---
        // Создаем временную конфигурацию, чтобы Serilog знал, куда писать логи ДО старта хоста.
        var bootstrapConfig = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true) // Просто добавляем его, если он есть
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(bootstrapConfig)
            .CreateBootstrapLogger();
        
        Log.Information("Serilog настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
        #endregion

        try {
            Log.Information("Start WebApplicationBuilder");
            
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(builder.Environment.IsDevelopment() ? 7002 : 8080); });
            
            Log.Information("WebApplicationBuilder создан за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            #region Logging preferences
            builder.Logging.ClearProviders();
            builder.Host.UseSerilog((context, loggerConfiguration) => {
                loggerConfiguration
                    .ReadFrom.Configuration(context.Configuration)
                    .Enrich.FromLogContext()
                    .Enrich.WithProcessId()
                    .Enrich.WithThreadId()
                    .Enrich.With<EnrichWithSourceClass>()
                    .Enrich.WithCaller();
                if (context.HostingEnvironment.IsDevelopment())
                    loggerConfiguration.WriteTo.File(
                        path: $"logs/app-{DateTime.Now:yyyyMMdd_HHmmss}.log",
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ThreadId}] {SourceContext:l}[{MemberName}] {Message:lj}{NewLine}{Exception}",
                        retainedFileCountLimit: 14);
            });
            #endregion
            #region Регистрация сервисов
            builder.Services.AddControllersWithViews();
            builder.Services.AddSignalR();
            
            // === Authentication (OIDC + Cookies) begin ===
            
            // 1. Настраиваем политики кук (Важно для OIDC в Docker)
            builder.Services.Configure<CookiePolicyOptions>(options =>
            {
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
                options.Secure = CookieSecurePolicy.Always; // Всегда Secure, так как у нас Cloudflare
            });
            
            builder.Services
                .AddAuthentication(options => {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(options =>
                {
                    options.Cookie.Name = ".BDC.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.None;
                })
                .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options => {
                    options.Authority = builder.Configuration["Authentication:B2C:Authorities:SignUpSignIn"];
                    options.ClientId = builder.Configuration["Authentication:B2C:ClientId"];
                    options.ClientSecret = builder.Configuration["Authentication:B2C:ClientSecret"];
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.SaveTokens = false;
                    //options.CallbackPath = builder.Configuration["Authentication:B2C:CallbackPath"] ?? "/signin-oidc";
                    options.SignedOutCallbackPath = builder.Configuration["Authentication:B2C:SignedOutCallbackPath"] ?? "/signout-callback-oidc";
                    options.Scope.Clear();
                    foreach (var scope in builder.Configuration.GetSection("Authentication:B2C:Scopes").Get<string[]>() ?? Array.Empty<string>()) {
                        options.Scope.Add(scope);
                    }
                });
            builder.Services.AddScoped<IClaimsTransformation, IdentityProviderRoleClaimsTransformation>();

            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.AddPolicy(DataManagerAuthorizationPolicies.Viewer, policy =>
                    policy.RequireRole(DataManagerRoles.Viewer, DataManagerRoles.Operator, DataManagerRoles.Admin));
                options.AddPolicy(DataManagerAuthorizationPolicies.Operator, policy =>
                    policy.RequireRole(DataManagerRoles.Operator, DataManagerRoles.Admin));
                options.AddPolicy(DataManagerAuthorizationPolicies.Admin, policy =>
                    policy.RequireRole(DataManagerRoles.Admin));
            });
            // === Authentication (OIDC + Cookies) end ===
            
            // Настройка заголовков для Traefik/Cloudflare
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedProto |
                    ForwardedHeaders.XForwardedHost;
                // Очищаем, чтобы доверять заголовкам из Docker-сети
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });
            
            builder.Services.Configure<ArchivesSettings>(builder.Configuration.GetSection("ArchivesSettings"));
            builder.Services.AddHttpClient("BinanceArchive", client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    client.DefaultRequestHeaders.Add("User-Agent", "BinanceDataCollector/1.0");
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = 10, // Ограничиваем подключения к Binance
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15), // Переиспользование соединений
                    ConnectTimeout = TimeSpan.FromSeconds(30),
                    ResponseDrainTimeout = TimeSpan.FromSeconds(10)
                });
           
            builder.Services.AddSingleton<IPathProvider, PathProvider>();
            
            // TODO: [Refactor] переделать на polly -> RabbitMqStatusNotifier
            // См. Issue #1
            builder.Services.AddSingleton<IStatusNotifier>(sp => {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return RabbitMqStatusNotifier.CreateAsync(configuration).GetAwaiter().GetResult(); 
            });
            builder.Services.AddScoped<ITradeRepository, TradeRepository>();
            builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
            builder.Services.AddScoped<ITrackedSymbolRepository, TrackedSymbolRepository>();
            builder.Services.AddScoped<IDataQualityRepository, DataQualityRepository>();
            builder.Services.AddScoped<IChartRepository, ChartRepository>();
            builder.Services.AddTransient<IChartIndicatorService, ChartIndicatorService>();
            builder.Services.AddScoped<IArchiveService, ArchiveService>();
            builder.Services.AddHostedService<RabbitMQListenerService>();
            builder.Services.AddScoped<IDatabaseMonitoringService, DatabaseMonitoringService>();
            Log.Information("Все сервисы зарегистрированы за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            #endregion
            #region Настройка Hangfire
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(
                    options => options.UseNpgsqlConnection(
                        builder.Configuration.GetConnectionString("HangfireConnection")),
                    new PostgreSqlStorageOptions
                    {
                        // Установщик схемы выключен — как в Worker. PgBouncer в transaction-режиме
                        // роняет session advisory locks, которыми Hangfire страхует создание схемы,
                        // и установщик падает на старте (на чистой БД он повторно накатывал
                        // миграции и спотыкался о already-existing колонку). Схема заводится
                        // out of band init-скриптом, приложение её только использует.
                        PrepareSchemaIfNecessary = false,
                        SchemaName = "hangfire"
                    }));
            #endregion
            
            builder.Services.AddHealthChecks()
                .AddNpgSql(
                    builder.Configuration.GetConnectionString("DefaultConnection")!,
                    name: "pgbouncer",
                    timeout: TimeSpan.FromSeconds(5)); 
            
            // Дамп конфигурации содержит пароли и B2C client secret. В Production логи
            // уходят в Seq — печатаем только локально, под отладчиком или в Development.
            if (Debugger.IsAttached || builder.Environment.IsDevelopment())
            {
                PrintConfiguration(builder.Configuration);
            }


            #region Общее место для хранения ключей
            var keysPath = builder.Environment.IsProduction()
                ? "/opt/bdc_data/keys"
                : Path.Combine(builder.Environment.ContentRootPath, "keys");

            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
                .SetApplicationName("BinanceDataCollector.DataManager");
            #endregion
            var app = builder.Build();
            app.UseForwardedHeaders();
            
            // ЖЕСТКИЙ ФИКС ДЛЯ ЦИКЛИЧЕСКОГО РЕДИРЕКТА
            // Заставляем приложение думать, что оно работает по HTTPS (так как SSL снял Cloudflare/Traefik)

            if (app.Environment.IsProduction())
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Scheme = "https";
                    await next();
                });
            }
            // ========================================
            
            Log.Information(
                "SERVICE STARTED {@Service}",
                new
                {
                    ServiceName = builder.Environment.ApplicationName.Substring(builder.Environment.ApplicationName.LastIndexOf('.') + 1),
                    ServiceRole = builder.Configuration["SERVICE_ROLE"] ?? "unknown",
                    Environment = builder.Environment.EnvironmentName,
                    Version = builder.Configuration["APP_VERSION"] ?? "unknown",
                    MachineName = Environment.MachineName,
                    ProcessId = Environment.ProcessId,
                    StartedAtUtc = DateTime.UtcNow,

                    ListeningUrls = builder.Configuration["ASPNETCORE_URLS"],
                    BehindReverseProxy = true,
                    TlsTermination = "Traefik/Cloudflare",

                    HealthLiveEndpoint = "/health/live",
                    HealthReadyEndpoint = "/health/ready",
                    HealthReadyDependsOn = new[] { "PostgreSQL" },
                    HealthIgnoredDependencies = new[] { "RabbitMQ" }
                }
            );
            
            Log.Information("Приложение собрано (Build) за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            app.UseMiddleware<MemoryUsageLoggingMiddleware>();
            if (!app.Environment.IsDevelopment()) {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            //app.UseHttpsRedirection();
            app.UseStaticFiles();

            // Middleware pipeline
            app.UseRouting();
            app.UseCookiePolicy(); // Активируем политики кук ПЕРЕД аутентификацией
            app.UseAuthentication();

            app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = new[] { new AdminHangfireAuthorizationFilter() } });

            app.UseAuthorization();
            app.MapHub<ArchiveStatusHub>("/archiveStatusHub");
            app.MapDefaultControllerRoute();

            app.MapHealthChecks("/health/live", new HealthCheckOptions {
                Predicate = _ => false
            }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new HealthCheckOptions {
                Predicate = _ => true
            }).AllowAnonymous();
            
            Log.Information(
                "SERVICE READY {@Ready}",
                new
                {
                    ServiceName = builder.Environment.ApplicationName.Substring(builder.Environment.ApplicationName.LastIndexOf('.') + 1),
                    ReadyAtUtc = DateTime.UtcNow,
                    ReadyDependsOn = new[] { "PostgreSQL" }
                }
            );
            
            #region logging
            Log.Information("Веб-пайплайн настроен за {Elapsed} мс.", startupStopwatch.ElapsedMilliseconds);
            startupStopwatch.Stop();
            Log.Information("Запуск хоста (app.Run)...");
            #endregion

            if (app.Environment.IsDevelopment())
            {
                app.Lifetime.ApplicationStarted.Register(() =>
                    Process.Start(new ProcessStartInfo("http://localhost:7002/hangfire") { UseShellExecute = true }));
            }

            app.Run();
        }
        catch (Exception ex) {
            Log.Fatal(ex, "Хост завершился с непредвиденной ошибкой");
            // TODO write to a file
        }
        finally {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Полный дамп конфигурации — включая строки подключения, пароли и B2C client secret.
    /// Вызывать ТОЛЬКО в Development или под отладчиком: в Production логи уезжают в Seq,
    /// и секреты оказались бы в общем хранилище логов.
    /// </summary>
    private static void PrintConfiguration(IConfiguration configuration)
    {
        Console.WriteLine("--- Configuration Debug View ---");
        foreach (var a in configuration.AsEnumerable())
        {
            Console.WriteLine($"{a.Key} = {a.Value}");
        }
        Console.WriteLine("--- End Configuration Debug View ---");
    }
}