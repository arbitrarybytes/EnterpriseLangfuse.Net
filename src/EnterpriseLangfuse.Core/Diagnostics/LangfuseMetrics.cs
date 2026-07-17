using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EnterpriseLangfuse.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible instruments for the framework's own health.
/// </summary>
/// <remarks>
/// Deliberately measures the resilience machinery — cache hit rate, fallback serves, dropped
/// telemetry — rather than the LLM calls themselves. Those go to Langfuse; these answer "is the
/// framework healthy?" and are the signals worth alerting on: a rising <c>prompt.fallback</c> rate
/// means the application is serving stale prompts, and any <c>telemetry.dropped</c> means traces are
/// being lost.
/// </remarks>
public static class LangfuseMetrics
{
    /// <summary>Meter name to enable in your OpenTelemetry pipeline.</summary>
    public const string MeterName = "EnterpriseLangfuse";

    /// <summary>ActivitySource name for spans this framework emits.</summary>
    public const string ActivitySourceName = "EnterpriseLangfuse";

    internal static readonly Meter Meter = new(MeterName, ThisAssembly.Version);

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, ThisAssembly.Version);

    internal static readonly Counter<long> PromptCacheHits =
        Meter.CreateCounter<long>("enterpriselangfuse.prompt.cache_hits", unit: "{request}", description: "Prompt requests served from the L1 cache.");

    internal static readonly Counter<long> PromptCacheMisses =
        Meter.CreateCounter<long>("enterpriselangfuse.prompt.cache_misses", unit: "{request}", description: "Prompt requests that had to leave the L1 cache.");

    internal static readonly Counter<long> PromptFallbacks =
        Meter.CreateCounter<long>("enterpriselangfuse.prompt.fallbacks", unit: "{request}", description: "Prompt requests served from an embedded fallback because Langfuse could not answer.");

    internal static readonly Counter<long> TelemetryEnqueued =
        Meter.CreateCounter<long>("enterpriselangfuse.telemetry.enqueued", unit: "{event}", description: "Telemetry events accepted onto the background queue.");

    internal static readonly Counter<long> TelemetryDropped =
        Meter.CreateCounter<long>("enterpriselangfuse.telemetry.dropped", unit: "{event}", description: "Telemetry events discarded because the queue was full.");

    internal static readonly Counter<long> TelemetryDispatched =
        Meter.CreateCounter<long>("enterpriselangfuse.telemetry.dispatched", unit: "{event}", description: "Telemetry events successfully delivered to Langfuse.");

    internal static readonly Histogram<double> IngestionDuration =
        Meter.CreateHistogram<double>("enterpriselangfuse.telemetry.ingestion.duration", unit: "ms", description: "Duration of ingestion batch requests.");
}

/// <summary>Assembly version, resolved once without reflection on the hot path.</summary>
internal static class ThisAssembly
{
    public const string Version = "0.1.0";
}
