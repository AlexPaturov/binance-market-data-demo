using System.Diagnostics;
using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Models;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Controllers
{
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
            _logger.LogInformation("Начало обработки запроса Index...");
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            try
            {
                // --- ЗАПУСКАЕМ ВСЕ АСИНХРОННЫЕ ЗАПРОСЫ ПАРАЛЛЕЛЬНО ---
                var activeSymbolsTask = _symbolRepo.GetActiveSymbolsAsync();
                var lastTradeTask = _tradeRepo.GetLastTradeAsync();
                var mainDbDetailsTask = _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics");
                var hangfireDbDetailsTask = _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics_jobs");

                // Ожидаем завершения всех запросов
                await Task.WhenAll(activeSymbolsTask, lastTradeTask, mainDbDetailsTask, hangfireDbDetailsTask);
                
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
                    HangfireServers = hangfireServers
                };

                stopwatch.Stop();
                _logger.LogInformation("Завершение обработки запроса Index за {TotalElapsed} мс.", stopwatch.ElapsedMilliseconds);

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке данных для главной страницы.");
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

        public async Task<IActionResult> GetHangfireDbDetails()
        {
            var details = await _dbMonitoringService.GetDatabaseDetailsAsync("market_analytics_jobs");
            return PartialView("~/Views/Shared/_DatabaseDetailsPartial.cshtml", details);
        }
        
        // public async Task<IActionResult> GetPostgresConnections() { ... }
    }
}