using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace BinanceDataCollector.Worker.Common;

public static class ShortSourceContextExtensions
{
    public static LoggerConfiguration WithShortSourceContext(
        this LoggerEnrichmentConfiguration enrich)
    {
        return enrich.With<ShortSourceContextEnricher>();
    }
}

public class ShortSourceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var value))
            return;

        var full = value.ToString().Trim('"');
        var shortName = full.Split('.').Last(); // только имя класса

        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty("ShortSourceContext", shortName));
    }
}

