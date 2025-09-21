using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.DataManager.Models;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace BinanceDataCollector.DataManager.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ITrackedSymbolRepository _symbolRepo;
        private readonly ITradeRepository _tradeRepo;
        private readonly IMonitoringApi _hangfireApi; // API для доступа к данным Hangfire

        public HomeController(
            ILogger<HomeController> logger,
            ITrackedSymbolRepository symbolRepo,
            ITradeRepository tradeRepo)
        {
            _logger = logger;
            _symbolRepo = symbolRepo;
            _tradeRepo = tradeRepo;
            _hangfireApi = JobStorage.Current.GetMonitoringApi(); // Получаем доступ к "внутренностям" Hangfire
        }

        public async Task<IActionResult> Index()
        {
            var viewModel = new HomeViewModel();                                    // 1. Создаем пустую ViewModel
            var stopwatch = new Stopwatch();
            _logger.LogInformation("Начало обработки запроса Index...");
           
            try
            {
                                               // 2. Наполняем ее данными
                stopwatch.Start();
                var activeSymbols = await _symbolRepo.GetActiveSymbolsAsync();      // Получаем количество активных символов
                viewModel.TrackedSymbolsCount = activeSymbols.Count();
                stopwatch.Stop();
                _logger.LogInformation("-> GetActiveSymbolsAsync выполнен за {Elapsed} мс.", stopwatch.ElapsedMilliseconds);

                stopwatch.Restart();
                viewModel.LastTrade = await _tradeRepo.GetLastTradeAsync();         // Получаем последнюю сделку (для любого символа)
                stopwatch.Stop();
                _logger.LogInformation("-> GetLastTradeAsync выполнен за {Elapsed} мс.", stopwatch.ElapsedMilliseconds);

                stopwatch.Restart();
                viewModel.HangfireServers = _hangfireApi.Servers()                  // Получаем информацию о серверах Hangfire
                    .Select(s => new ServerDto
                    {
                        Name = s.Name,
                        Queues = string.Join(", ", s.Queues),
                        WorkerCount = s.WorkersCount,
                        Heartbeat = s.Heartbeat?.ToUniversalTime() ?? DateTime.MinValue
                    })
                    .ToList();
                stopwatch.Stop();
                _logger.LogInformation("-> HangfireApi.Servers выполнен за {Elapsed} мс.", stopwatch.ElapsedMilliseconds);

                viewModel.SystemStatus = "Online";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при загрузке данных для главной страницы.");
                viewModel.SystemStatus = $"Error: {ex.Message}";
            }

            _logger.LogInformation("Завершение обработки запроса Index.");
            return View(viewModel);                                                  // 3. Передаем готовую ViewModel в представление
        }
    }
}
