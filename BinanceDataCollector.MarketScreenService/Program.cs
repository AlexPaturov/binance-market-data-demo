using CsvHelper;
using System.Globalization;
using System.Text;

namespace BinanceDataCollector.MarketScreenService;

public class Program
{
    static async Task Main(string[] args)
    {
        // 1. Получаем данные как и раньше
        var screener = new MarketScreener();
        var topPairs = await screener.FindTopPairsAsync(topN: 30, minQuoteVolumeInMillion: 20m);

        if (!topPairs.Any())
        {
            Console.WriteLine("Не найдено пар, соответствующих критериям. Вывод невозможен.");
            return;
        }

        // 2. Создаем экземпляр нашего экспортера
        var exporter = new DataExporter();

        // 3. Вызываем метод для вывода в консоль
        exporter.PrintToConsole(topPairs);

        // 4. Вызываем метод для сохранения в CSV
        // Создаем уникальное имя файла с текущей датой и временем
        string fileName = $"market_scan_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
        await exporter.SaveToCsvAsync(topPairs, fileName);

        Console.WriteLine("\nСканирование и экспорт завершены.");
    }
}
