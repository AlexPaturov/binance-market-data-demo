using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Application.Archives.Models;
using BinanceDataCollector.Application.Common;
using BinanceDataCollector.Domain.DTOs;
using BinanceDataCollector.Domain.Entities;
using BinanceDataCollector.Infrastructure.Persistence.Csv;
using BinanceDataCollector.Infrastructure.Persistence.Csv.Mappers;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.IO.Compression;
using System.Net;

namespace BinanceDataCollector.Infrastructure.Services;

/// <summary>
/// Адаптер между Trade and raw csv file from binance
/// </summary>
public class ArchiveService : IArchiveService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArchiveService> _logger;
    private readonly string _archivesPath; 

    public ArchiveService(
        IHttpClientFactory httpClientFactory,
        ILogger<ArchiveService> logger,
        IOptions<ArchivesSettings> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _archivesPath = options.Value.TradeArcihvesPath; // <-- Инициализируем путь
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
            HasHeaderRecord = false,
            DetectDelimiter = false,        // Отключаем автоматическое определение типов - все читаем как строки
            Delimiter = ",",                // Указываем разделитель явно 
            TrimOptions = TrimOptions.Trim, // Можно отключить trim для избежания потери ведущих нулей
            MissingFieldFound = null,       // Настройки для работы с большими числами
            HeaderValidated = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var csvRecord = csv.GetRecord<BinanceCsvTradeRecord>();
            var trade = TradeMapper.ToDomainEntity(csvRecord, symbol);
            yield return trade;
        }
    }

    public async Task<bool> DownloadArchiveToStreamAsync(string symbol, DateOnly date, Stream fileStream, CancellationToken none)
    {
        var url = $"https://data.binance.vision/data/spot/daily/trades/{symbol}/{symbol}-trades-{date:yyyy-MM-dd}.zip";
        _logger.LogInformation("Скачиваем архив: {Url}", url);

        using var httpClient = _httpClientFactory.CreateClient("BinanceArchive");
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, none);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Архив не найден (404) по адресу: {Url}", url);
            return false; // Возвращаем false, не бросаем исключение
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(none);
        await responseStream.CopyToAsync(fileStream, none); // Копируем содержимое в переданный FileStream
        return true;
    }

    public Task<List<ArchivedFileInfo>> GetArchivedFilesAsync()
    {
        var directory = new DirectoryInfo(_archivesPath);
        if (!directory.Exists)
        {
            _logger.LogWarning("Директория для архивов не найдена по пути: {Path}", _archivesPath);
            return Task.FromResult(new List<ArchivedFileInfo>());
        }

        var files = directory.GetFiles("*.zip")
            .Select(fileInfo => {
                var (symbol, date) = ArchiveFileNameParser.Parse(fileInfo.Name);
                return new ArchivedFileInfo
                {
                    FileName = fileInfo.Name,
                    Symbol = symbol,
                    Date = date,
                    SizeBytes = fileInfo.Length
                };
            })
            .OrderByDescending(f => f.Date)
            .ToList();

        return Task.FromResult(files);
    }

    public async IAsyncEnumerable<Trade> ParseTradesFromLocalZipAsync(string zipFilePath, string symbol, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Парсим локальный архив: {Path}", zipFilePath);

        await using var zipStream = File.OpenRead(zipFilePath); // <-- Открываем локальный файл
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entry = archive.Entries.FirstOrDefault();
        if (entry == null) yield break;

        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, leaveOpen: false);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            DetectDelimiter = false,        // Отключаем автоматическое определение типов - все читаем как строки
            Delimiter = ",",                // Указываем разделитель явно 
            TrimOptions = TrimOptions.Trim, // Можно отключить trim для избежания потери ведущих нулей
            MissingFieldFound = null,       // Настройки для работы с большими числами
            HeaderValidated = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var csvRecord = csv.GetRecord<BinanceCsvTradeRecord>();
            var trade = TradeMapper.ToDomainEntity(csvRecord, symbol);
            yield return trade;
        }
    }

    public async IAsyncEnumerable<Trade> ParseTradesFromCsvStreamAsync(Stream csvStream, string symbol, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(csvStream);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) 
        {
            HasHeaderRecord = false,
            DetectDelimiter = false,        // Отключаем автоматическое определение типов - все читаем как строки
            Delimiter = ",",                // Указываем разделитель явно 
            TrimOptions = TrimOptions.Trim, // Можно отключить trim для избежания потери ведущих нулей
            MissingFieldFound = null,       // Настройки для работы с большими числами
            HeaderValidated = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var csvRecord = csv.GetRecord<BinanceCsvTradeRecord>();
            var trade = TradeMapper.ToDomainEntity(csvRecord, symbol);
            yield return trade;
        }
    }

    public async Task<List<Trade>> InspectArchiveContentAsync(string zipFileName)
    {
        var filePath = Path.Combine(_archivesPath, zipFileName);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Архив {FileName} не найден для инспекции.", zipFileName);
            return new List<Trade>();
        }

        var trades = new List<Trade>();
        var (symbol, date) = ArchiveFileNameParser.Parse(zipFileName);

        await using var zipStream = File.OpenRead(filePath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault();

        if (entry == null) return trades;

        await using var entryStream = entry.Open();
        await foreach (var trade in ParseTradesFromCsvStreamAsync(entryStream, symbol, CancellationToken.None))
        {
            trades.Add(trade);
        }

        return trades;
    }
}
