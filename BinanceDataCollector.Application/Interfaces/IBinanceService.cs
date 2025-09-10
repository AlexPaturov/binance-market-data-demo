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
    /// Принимает список символов и Action<Trade> Подписывается на поток сделок в реальном времени для всех.
    /// </summary>
    /// <param name="symbols">Символ для отслеживания.</param>
    /// <param name="onTradeReceived">Действие, которое будет выполняться при получении каждой новой сделки.</param>
    /// <param name="cancellationToken">Токен для отмены подписки.</param>
    Task SubscribeToMultipleTradesAsync(IEnumerable<string> symbols, Action<Trade> onTradeReceived, CancellationToken cancellationToken);

    /// <summary>
    /// Получает информацию о всех символах, торгуемых на бирже.
    /// </summary>
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Коллекция символов.</returns>
    Task<IEnumerable<BinanceSymbol>> GetExchangeSymbolsAsync(CancellationToken cancellationToken = default);

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
    Task<FetchResult> GetHistoricalAggTradesByTime(
        string symbol,
        DateTime startTime,
        CancellationToken cancellationToken);

    /// <summary>
    /// Загружаем сделку от указанного id и не более 1000 за раз
    /// </summary>
    /// <param name="symbol"></param>
    /// <param name="fromId"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="limit"></param>
    /// <returns></returns>
    Task<FetchResult> GetHistoricalAggTradesById(string symbol, long fromId, CancellationToken cancellationToken, int limit = 1000);

    /// <summary>
    /// Загружает историю СЫРЫХ сделок, начиная с указанного TradeId.
    /// Используется для точного заполнения дыр в данных.
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="fromId">ID сделки, с которой начать поиск.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <param name="limit">Количество записей для загрузки (макс. 1000).</param>
    /// <returns>Результат загрузки со списком сырых сделок.</returns>
    Task<FetchResult> GetHistoricalRawTradesAsync(string symbol, long fromId, CancellationToken cancellationToken, int limit = 1000);
}
