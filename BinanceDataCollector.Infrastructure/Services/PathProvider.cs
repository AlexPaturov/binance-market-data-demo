using BinanceDataCollector.Application.Archives.Interfaces;
using BinanceDataCollector.Domain.DTOs;
using Microsoft.Extensions.Options;

namespace BinanceDataCollector.Infrastructure.Services;

public class PathProvider : IPathProvider
{
    private readonly string _baseDataPath;
    private readonly ArchivesSettings _settings;

    public PathProvider(IOptions<ArchivesSettings> settings)
    {
        _settings = settings.Value;
        
        // 1. Находим "правильную" базовую папку для данных приложения
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        
        // 2. Создаем внутри нее папку для нашего приложения
        _baseDataPath = Path.Combine(localAppData, "BinanceDataCollector");
    }
    
    private string GetBaseDataPath()
    {
        // Используем директивы компиляции, чтобы выбрать разную логику для разных платформ
#if ANDROID
        // Для Android можно выбрать внешнее хранилище, если оно доступно
        // и права получены. Это сложная логика, здесь упрощенный пример.
        return Android.App.Application.Context.GetExternalFilesDir(null).AbsolutePath;
#elif IOS
        // Для iOS - всегда приватная папка
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
        // Для десктопных ОС (Windows, Linux, macOS) - наше старое решение
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _settings.RootDirectoryName);
#endif
    }

    // Этот метод будет возвращать D:\lex\AppData\Local\BinanceDataCollector\Trades\Downloaded на Windows
    // и /home/lex/.local/share/BinanceDataCollector/Trades/Downloaded на Linux
    public string GetTradeArchivesPath()
    {
        // Используем директивы компиляции, чтобы выбрать разную логику для разных платформ
#if ANDROID
        // Для Android можно выбрать внешнее хранилище, если оно доступно
        // и права получены. Это сложная логика, здесь упрощенный пример.
        return Android.App.Application.Context.GetExternalFilesDir(null).AbsolutePath;
#elif IOS
        // Для iOS - всегда приватная папка
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
        // Для десктопных ОС (Windows, Linux, macOS) - наше старое решение
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            _settings.TradeArchivesRelativePath);
        
        // return Path.Combine(
        //     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        //     "BinanceDataCollector");
#endif
        
    }

    public string GetTradeUnpackedPath()
    {
        // Используем директивы компиляции, чтобы выбрать разную логику для разных платформ 
#if ANDROID
        // Для Android можно выбрать внешнее хранилище, если оно доступно
        // и права получены. Это сложная логика, здесь упрощенный пример.
        return Android.App.Application.Context.GetExternalFilesDir(null).AbsolutePath;
#elif IOS
        // Для iOS - всегда приватная папка
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
        // Для десктопных ОС (Windows, Linux, macOS) - наше старое решение
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            _settings.TradeUnpackedRelativePath);
        
        // return Path.Combine(
        //     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        //     "BinanceDataCollector");
#endif
        
        // return Path.Combine(_baseDataPath, _settings.TradeUnpackedRelativePath);
    }

    public string GetOhlcvArchivesPath()
    {
        return Path.Combine(_baseDataPath, _settings.OhlcvArchivesPath);
    }

    public string GetOhlcvUnpackedPath()
    {
        return Path.Combine(_baseDataPath, _settings.OhlcvUnpackedRelativePath);
    }
}