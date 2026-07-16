using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Interfaces;

/// <summary>
/// для работы со свечами
/// </summary>
public interface IOhlcvRepository
{
    /// <summary>
    /// "Резервирует" и возвращает новую пачку свечей для обработки
    /// </summary>
    /// <param name="batchSize"></param>
    /// <returns></returns>
    Task<IEnumerable<Ohlcv>> ClaimNewKlinesForProcessingAsync(int batchSize);

    /// <summary>
    /// Помечает свечи как полностью обработанные.
    ///
    /// Ключ свечи составной — (Symbol, OpenTime). Пометка только по времени задевала бы
    /// свечи ДРУГИХ символов за ту же минуту, по которым индикаторы ещё не считались.
    /// </summary>
    Task MarkKlinesAsProcessedAsync(IEnumerable<Ohlcv> klines);

    /// <summary>
    /// Получает "хвост" исторических данных (свечей), необходимых для "прогрева" индикаторов.
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="beforeTime">Временная метка, ДО которой нужно выбрать данные.</param>
    /// <param name="limit">Количество свечей для выборки.</param>
    /// <returns>Коллекция исторических свечей.</returns>
    Task<IEnumerable<Ohlcv>> GetWarmupKlinesAsync(string symbol, long beforeTime, int limit);


    Task<long?> GetLastKlineOpenTimeAsync(string symbol);

    /// <summary>
    /// Время открытия самой свежей свечи по всем символам. NULL — свечей нет вовсе.
    /// Разница с текущим временем — лаг конвейера агрегации; по нему импорт архивов
    /// решает, есть ли у конвейера запас (backpressure).
    /// </summary>
    Task<long?> GetNewestCandleOpenTimeAsync();

    Task BulkUpsertAsync(IEnumerable<Ohlcv> klines);
}
