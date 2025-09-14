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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArchiveService> _logger;

    public ArchiveService(
        IHttpClientFactory httpClientFactory,
        ILogger<ArchiveService> logger
    )
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async IAsyncEnumerable<Trade> DownloadAndParseTradesAsync(string symbol, DateOnly date, CancellationToken cancellationToken)
    {
        var url = $"https://data.binance.vision/data/spot/daily/trades/{symbol}/{symbol}-trades-{date:yyyy-MM-dd}.zip";
        _logger.LogInformation("Скачиваем архив: {Url}", url);

        using var httpClient = _httpClientFactory.CreateClient("BinanceArchive");
        await using var zipStream = await httpClient.GetStreamAsync(url, cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        var entry = archive.Entries.FirstOrDefault();
        if (entry == null)
        {
            yield break;
        }

        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, leaveOpen: false);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false
        };
        using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = csv.GetRecord<BinanceCsvTradeRecord>();

            yield return new Trade
            {
                TradeId = record.Id,
                Symbol = symbol,
                Price = record.Price,
                Quantity = record.Quantity,
                QuoteQuantity = record.QuoteQuantity,
                TradeTime = record.Time,
                IsBuyerMaker = record.IsBuyerMaker,
                IsBestMatch = record.IsBestMatch ?? false
            };
        }
    }
}
