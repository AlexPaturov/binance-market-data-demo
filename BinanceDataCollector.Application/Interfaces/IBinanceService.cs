using Binance.Net.Enums;
using Binance.Net.Objects.Models.Spot;
using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// Абстракция для взаимодействия с API Binance.
/// Скрывает детали реализации библиотеки Binance.Net.
/// </summary>
public interface IBinanceService
{
    /// <summary>
    /// Подписывается на поток сделок в реальном времени для одного символа.
    /// </summary>
    /// <param name="symbol">Символ для отслеживания.</param>
    /// <param name="onTradeReceived">Действие, которое будет выполняться при получении каждой новой сделки.</param>
    /// <param name="cancellationToken">Токен для отмены подписки.</param>
    Task SubscribeToTradesAsync(string symbol, Func<Trade, Task> onTradeReceived, CancellationToken cancellationToken);

    /// <summary>
    /// Получает информацию о всех символах, торгуемых на бирже.
    /// </summary>
    /// <returns>Коллекция символов.</returns>
    Task<IEnumerable<BinanceSymbol>> GetExchangeSymbolsAsync();

    /// <summary>
    /// Получает 24-часовую статистику (тикеры) для всех символов.
    /// </summary>
    /// <returns>Коллекция тикеров со статистикой.</returns>
    Task<IEnumerable<Binance24HPrice>> Get24hTickerStatisticsAsync();

    /// <summary>
    /// Загружает исторические свечи (Klines) за указанный период.
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="interval">Интервал свечи.</param>
    /// <param name="startTime">Начало периода.</param>
    /// <param name="endTime">Конец периода.</param>
    /// <param name="cancellationToken">Токен для отмены.</param>
    /// <returns>Коллекция исторических свечей.</returns>
    Task<IEnumerable<BinanceSpotKline>> GetHistoricalKlinesAsync(
        string symbol,
        KlineInterval interval,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken);

    /// <summary>
    /// Загружает историю агрегированных сделок пачками по 1000
    /// </summary>
    /// <returns>Коллекция исторических сделок.</returns>
    Task<FetchResult> GetHistoricalAggTradesAsync(
        string symbol,
        DateTime startTime,
        CancellationToken cancellationToken);

}
