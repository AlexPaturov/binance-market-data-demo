namespace BinanceDataCollector.Domain.Entities;

// Модель для представления "дыры"
public record DataGap(long GapStart, long GapEnd);
