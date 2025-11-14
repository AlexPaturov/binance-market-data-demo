namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Перечисление для описания статуса операции
/// </summary>
public enum FetchStatus
{
    Success,      // Все прошло успешно
    ApiLimit,     // Binance ответил ошибкой о превышении лимита
    GeneralError  // Любая другая ошибка
}

/// <summary>
/// Класс-обертка для результатов запроса к API.
/// </summary>
public class FetchResult
{
    public FetchStatus Status { get; init; }
    public List<Trade> Data { get; init; } = new();

    // Статические "фабричные" методы для удобного создания результатов
    public static FetchResult SuccessResult(List<Trade> data) => new() { Status = FetchStatus.Success, Data = data };
    public static FetchResult ApiLimitResult() => new() { Status = FetchStatus.ApiLimit };
    public static FetchResult ErrorResult() => new() { Status = FetchStatus.GeneralError };
}

