using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;

namespace BinanceDataCollector.Infrastructure.Persistence.Csv.Converters;

/// <summary>
/// Конвертер для безопасного чтения длинных чисел из CSV.
/// Размещен в Infrastructure, так как это техническая деталь работы с CSV.
/// </summary>
public class SafeLongConverter : DefaultTypeConverter
{
    public override object ConvertFromString(string text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0L;

        text = text.Trim();

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
        {
            return result;
        }

        throw new TypeConverterException(this, memberMapData, text, row.Context, $"Unable to convert '{text}' to long");
    }
}