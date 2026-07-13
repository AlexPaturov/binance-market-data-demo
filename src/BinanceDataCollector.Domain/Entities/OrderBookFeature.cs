namespace BinanceDataCollector.Domain.Entities;

/// <summary>
/// Поминутные фичи стакана. Сырой L2 не хранится — из книги в памяти считаются
/// готовые числа и пишутся раз в минуту.
/// </summary>
public class OrderBookFeature
{
    public required string Symbol { get; set; }

    /// <summary>Начало минуты, Unix-мс. Та же сетка, что у свечей.</summary>
    public long OpenTime { get; set; }

    public decimal MidPrice { get; set; }
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public decimal SpreadAbs { get; set; }
    public decimal SpreadBps { get; set; }

    /// <summary>Дисбаланс книги: (bid - ask) / (bid + ask) по топ-N уровням. От -1 до +1.</summary>
    public decimal Imbalance { get; set; }

    /// <summary>Объём заявок в пределах 0.1% от mid.</summary>
    public decimal BidDepth01 { get; set; }
    public decimal AskDepth01 { get; set; }

    /// <summary>0.5% от mid.</summary>
    public decimal BidDepth05 { get; set; }
    public decimal AskDepth05 { get; set; }

    /// <summary>1.0% от mid.</summary>
    public decimal BidDepth10 { get; set; }
    public decimal AskDepth10 { get; set; }

    /// <summary>Крупнейшая одиночная заявка на стороне покупки и её удалённость от mid.</summary>
    public decimal MaxBidWall { get; set; }
    public decimal MaxBidWallDistBps { get; set; }

    public decimal MaxAskWall { get; set; }
    public decimal MaxAskWallDistBps { get; set; }

    /// <summary>Сколько раз книга обновилась за минуту — прокси нервозности рынка.</summary>
    public int UpdateCount { get; set; }

    /// <summary>
    /// Сколько снимков книги усреднено за минуту. Меньше ожидаемого — были разрывы
    /// связи или ресинк, и фичи за эту минуту менее надёжны.
    /// </summary>
    public int SampleCount { get; set; }
}

/// <summary>Один снимок стакана. Из усреднения снимков за минуту получается <see cref="OrderBookFeature"/>.</summary>
public class OrderBookSnapshot
{
    public decimal MidPrice { get; set; }
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public decimal SpreadAbs { get; set; }
    public decimal SpreadBps { get; set; }
    public decimal Imbalance { get; set; }
    public decimal BidDepth01 { get; set; }
    public decimal AskDepth01 { get; set; }
    public decimal BidDepth05 { get; set; }
    public decimal AskDepth05 { get; set; }
    public decimal BidDepth10 { get; set; }
    public decimal AskDepth10 { get; set; }
    public decimal MaxBidWall { get; set; }
    public decimal MaxBidWallDistBps { get; set; }
    public decimal MaxAskWall { get; set; }
    public decimal MaxAskWallDistBps { get; set; }
}

/// <summary>Уровень стакана: цена и объём.</summary>
public readonly record struct OrderBookLevel(decimal Price, decimal Quantity);
