namespace BinanceDataCollector.Application.Analytics.Models;

/// <summary>Свеча выбранного таймфрейма, собранная из минутных.</summary>
public class ChartCandle
{
    /// <summary>Начало бара, Unix-мс (UTC).</summary>
    public long OpenTime { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Volume { get; set; }
}

/// <summary>Точка индикатора, привязанная к бару.</summary>
public class IndicatorPoint
{
    public long OpenTime { get; set; }
    public decimal? Value { get; set; }
}

/// <summary>
/// Индикаторы, посчитанные для конкретного таймфрейма.
/// RSI/MACD/MA пересчитываются из агрегированных свечей (значения из `Ohlcv_Features`
/// посчитаны на минутках и для старших таймфреймов неверны). CVD — кумулятивный ряд,
/// поэтому берётся из `Ohlcv_Features` как значение на конец бара.
/// </summary>
public class ChartIndicators
{
    public List<IndicatorPoint> Rsi { get; set; } = new();
    public List<IndicatorPoint> MacdLine { get; set; } = new();
    public List<IndicatorPoint> MacdSignal { get; set; } = new();
    public List<IndicatorPoint> MacdHistogram { get; set; } = new();
    public List<IndicatorPoint> MaFast { get; set; } = new();
    public List<IndicatorPoint> MaSlow { get; set; } = new();
    public List<IndicatorPoint> Cvd { get; set; } = new();
}

/// <summary>Настройки индикаторов, приходят со страницы.</summary>
public class IndicatorSettings
{
    public int RsiPeriod { get; set; } = 14;
    public int MacdFast { get; set; } = 12;
    public int MacdSlow { get; set; } = 26;
    public int MacdSignal { get; set; } = 9;

    /// <summary>Период MA в барах текущего таймфрейма (не в минутах).</summary>
    public int MaFastPeriod { get; set; } = 50;
    public int MaSlowPeriod { get; set; } = 200;

    /// <summary>EMA вместо SMA для обеих скользящих.</summary>
    public bool UseEma { get; set; }
}

/// <summary>Ответ на запрос данных графика.</summary>
public class ChartData
{
    public required string Symbol { get; set; }
    public required string Timeframe { get; set; }
    public List<ChartCandle> Candles { get; set; } = new();
    public ChartIndicators Indicators { get; set; } = new();
}
