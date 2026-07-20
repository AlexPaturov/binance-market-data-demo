using System.Diagnostics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Models;
using BinanceDataCollector.DataManager.Common.Auth;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers
{
    [Authorize(Policy = DataManagerAuthorizationPolicies.Viewer)]
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ITrackedSymbolRepository _symbolRepo;
        private readonly ITradeRepository _tradeRepo;
        private readonly IMonitoringApi _hangfireApi;
        private readonly IDatabaseMonitoringService _dbMonitoringService;

        public HomeController(
            ILogger<HomeController> logger,
            ITrackedSymbolRepository symbolRepo,
            ITradeRepository tradeRepo,
            IDatabaseMonitoringService dbMonitoringService)
        {
            _logger = logger;
            _symbolRepo = symbolRepo;
            _tradeRepo = tradeRepo;
            _hangfireApi = JobStorage.Current.GetMonitoringApi();
            _dbMonitoringService = dbMonitoringService;
        }

        public async Task<IActionResult> Index()
        {
            _logger.LogInformation(
                "REQUEST CONTEXT {@Ctx}",
                new {
                    Scheme = HttpContext.Request.Scheme,
                    Host = HttpContext.Request.Host.Value,
                    PathBase = HttpContext.Request.PathBase.Value
                });
            
            _logger.LogInformation("Processing Index request...");
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            try
            {
                // --- ЗАПУСКАЕМ ВСЕ АСИНХРОННЫЕ ЗАПРОСЫ ПАРАЛЛЕЛЬНО ---
                var activeSymbolsTask = _symbolRepo.GetActiveSymbolsAsync();
                var lastTradeTask = _tradeRepo.GetLastTradeAsync();
                var mainDbDetailsTask = _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics");
                var hangfireDbDetailsTask = _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics_jobs");
                var monthsTask = _dbMonitoringService.GetMonthPartitionsAsync("market_analytics");

                // Ожидаем завершения всех запросов
                await Task.WhenAll(activeSymbolsTask, lastTradeTask, mainDbDetailsTask, hangfireDbDetailsTask, monthsTask);
                
                // --- СИНХРОННЫЕ ЗАПРОСЫ ВЫПОЛНЯЕМ ПОСЛЕ ---
                var hangfireServers = _hangfireApi.Servers()
                    .Select(s => new ServerDto
                    {
                        Name = s.Name,
                        Queues = string.Join(", ", s.Queues),
                        WorkerCount = s.WorkersCount,
                        Heartbeat = s.Heartbeat?.ToUniversalTime() ?? DateTime.MinValue
                    })
                    .ToList();

                // --- СОБИРАЕМ ViewModel ИЗ РЕЗУЛЬТАТОВ ---
                var viewModel = new HomeViewModel
                {
                    SystemStatus = "Online",
                    TrackedSymbolsCount = (await activeSymbolsTask).Count(),
                    LastTrade = await lastTradeTask,
                    MainDbDetails = await mainDbDetailsTask,
                    HangfireDbDetails = await hangfireDbDetailsTask,
                    HangfireServers = hangfireServers,
                    Months = await monthsTask
                };

                stopwatch.Stop();
                _logger.LogInformation("Index request completed in {TotalElapsed} ms.", stopwatch.ElapsedMilliseconds);

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading data for the home page.");
                // Возвращаем пустую ViewModel с сообщением об ошибке
                var errorViewModel = new HomeViewModel
                {
                    SystemStatus = $"Error: {ex.Message}"
                };
                return View(errorViewModel);
            }
        }
        
        public async Task<IActionResult> GetMainDbDetails()
        {
            var details = await _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics");
            return PartialView("~/Views/Shared/_DatabaseDetailsPartial.cshtml", details);
        }

        // Помесячная сводка Trades — своя точка обновления (панель тикает раз в 120 с).
        public async Task<IActionResult> GetMonthPartitions()
        {
            var months = await _dbMonitoringService.GetMonthPartitionsAsync("market_analytics");
            return PartialView("~/Views/Shared/_MonthPartitionsPartial.cshtml", months);
        }

        public async Task<IActionResult> GetHangfireDbDetails()
        {
            var details = await _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics_jobs");
            return PartialView("~/Views/Shared/_DatabaseDetailsPartial.cshtml", details);
        }

        // Цель UseExceptionHandler("/Home/Error"). Без этого action путь 404-ил, и обработчик
        // исключений падал с InvalidOperationException, маскируя исходную ошибку.
        // AllowAnonymous — страница ошибки должна открываться даже когда проблема в авторизации.
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }
        
        // public async Task<IActionResult> GetPostgresConnections() { ... }
    }
}