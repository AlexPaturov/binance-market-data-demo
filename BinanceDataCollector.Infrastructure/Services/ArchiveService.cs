using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Domain.Entities;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO.Compression;

namespace BinanceDataCollector.Infrastructure.Services;

/// <summary>
/// Адаптер между Trade and raw csv file from binance
/// </summary>
public class ArchiveService : IArchiveService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ArchiveService> _logger;

    public ArchiveService(ILogger<ArchiveService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async Task<List<Trade>> DownloadAndParseTradesAsync(string symbol, DateOnly date, CancellationToken cancellationToken)
    {
        var url = $"https://data.binance.vision/data/spot/daily/trades/{symbol}/{symbol}-trades-{date:yyyy-MM-dd}.zip";
        _logger.LogInformation("Скачиваем архив: {Url}", url);

        // --- 1. Скачивание ---
        var zipStream = await _httpClient.GetStreamAsync(url, cancellationToken);

        // --- 2. Распаковка ---
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault();
        if (entry == null) return new List<Trade>();

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);

        // --- 3. Парсинг CSV с помощью CsvHelper ---
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Указываем, что в файле нет строки с заголовками
            HasHeaderRecord = false,
        };

        using var csv = new CsvReader(reader, config);

        // CsvHelper автоматически смапит колонки на свойства по атрибутам [Index]
        var records = csv.GetRecords<BinanceCsvTradeRecord>().ToList();

        // --- 4. Маппинг в нашу доменную модель Trade ---
        var trades = records.Select(t => new Trade
        {
            TradeId = t.Id,
            Symbol = symbol, // Символ мы знаем из параметра метода
            Price = t.Price,
            Quantity = t.Quantity,
            QuoteQuantity = t.QuoteQuantity,
            TradeTime = t.Time,
            IsBuyerMaker = t.IsBuyerMaker,
            // Используем null-coalescing оператор (??) для необязательного поля
            IsBestMatch = t.IsBestMatch ?? false
        }).ToList();

        _logger.LogInformation("Успешно импортировано {Count} сделок из архива.", trades.Count);
        return trades;
    }
}
