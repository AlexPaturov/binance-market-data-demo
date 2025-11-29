using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Infrastructure.Services;

public class PathProvider : IPathProvider
{
    private readonly ArchivesSettings _settings;

    // Абсолютный серверный путь (монтируется как volume в docker-compose)
    private const string ServerBasePath = "/opt/bdc_data";

    public PathProvider(IOptions<ArchivesSettings> settings)
    {
        _settings = settings.Value;
    }

    private string Map(string relativePath)
    {
        return Path.Combine(ServerBasePath, relativePath);
    }

    public string GetTradeArchivesPath() => Map(_settings.TradeArchivesRelativePath);
    public string GetTradeUnpackedPath() => Map(_settings.TradeUnpackedRelativePath);
    public string GetOhlcvArchivesPath() => Map(_settings.OhlcvArchivesRelativePath);
    public string GetOhlcvUnpackedPath() => Map(_settings.OhlcvUnpackedRelativePath);
}
