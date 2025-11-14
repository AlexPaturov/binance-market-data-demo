using Serilog.Core;
using Serilog.Events;

namespace BinanceDataCollector.DataManager.Common;

public class EnrichWithSourceClass : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Extract just the class from the end of the source context property
        if (logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContextValue)
            && sourceContextValue is ScalarValue scalarValue)
        {
            // Get source context as literal value using "l" serilog formatter, to avoid wrapping in quote characters
            string sourceContext = scalarValue.ToString("l", null);

            int start = sourceContext.LastIndexOf('.');
            start = (start >= 0) ? (start + 1) : 0;

            string sourceClass = sourceContext[start..];
            LogEventProperty enrichProperty = propertyFactory.CreateProperty("SourceClass", sourceClass);

            logEvent.AddOrUpdateProperty(enrichProperty);
        }
    }
}
