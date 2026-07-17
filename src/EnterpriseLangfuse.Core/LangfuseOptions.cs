using System.Reflection;

namespace EnterpriseLangfuse;

/// <summary>Configuration for <c>AddEnterpriseLangfuse</c>.</summary>
public sealed class LangfuseOptions
{
    /// <summary>The Langfuse project public key (<c>pk-lf-...</c>).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The Langfuse project secret key (<c>sk-lf-...</c>).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Langfuse host. Defaults to Langfuse Cloud; set this for self-hosted deployments.</summary>
    public Uri BaseUrl { get; set; } = new("https://cloud.langfuse.com");

    /// <summary>
    /// Environment tag applied to every emitted trace, e.g. <c>production</c> or <c>staging</c>.
    /// Lets one project separate traffic per deployment.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>Release/version tag applied to every trace, typically a git SHA.</summary>
    public string? Release { get; set; }

    /// <summary>
    /// How long a successfully fetched prompt is cached (L1). The spec's default is 60s: long enough
    /// to keep the API off the hot path, short enough that a prompt edit reaches production promptly.
    /// </summary>
    public TimeSpan PromptCacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long an embedded-fallback result is cached during an outage.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="PromptCacheDuration"/>. Caching a fallback for the full
    /// 60s would keep serving the stale embedded copy for a minute after Langfuse recovers; caching it
    /// briefly still prevents hammering a dead endpoint on every call.
    /// </remarks>
    public TimeSpan FallbackCacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to serve embedded <c>.prompt.yaml</c> resources when Langfuse is unreachable (L3).
    /// Disable only if you would rather fail loudly than serve a possibly stale prompt.
    /// </summary>
    public bool EnableOfflineFallback { get; set; } = true;

    /// <summary>
    /// Assemblies scanned for embedded <c>*.prompt.yaml</c> fallbacks. When empty, only the entry
    /// assembly is scanned — libraries that embed prompts must be added here explicitly.
    /// </summary>
    public IList<Assembly> FallbackAssemblies { get; } = [];

    /// <summary>Whether background telemetry is dispatched to Langfuse.</summary>
    public bool EnableTelemetry { get; set; } = true;

    /// <summary>
    /// Maximum events buffered before back-pressure applies. Bounded on purpose: an unbounded queue
    /// turns a Langfuse outage into an application memory leak.
    /// </summary>
    public int TelemetryQueueCapacity { get; set; } = 10_000;

    /// <summary>Maximum events per ingestion request.</summary>
    public int TelemetryBatchSize { get; set; } = 100;

    /// <summary>How long a partial batch waits before being flushed anyway.</summary>
    public TimeSpan TelemetryFlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <c>StopAsync</c> waits to drain queued telemetry at shutdown before giving up, so a
    /// dead Langfuse cannot hang a deployment.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Which event to discard when the queue is full.</summary>
    /// <remarks>
    /// There is deliberately no "block the caller" policy. <see cref="Telemetry.ILangfuseTelemetry"/>
    /// is synchronous and contractually never blocks, so back-pressure could not be honoured without
    /// breaking that promise — and an option that quietly fails to do what it says is worse than no
    /// option. Losing an observation is strictly better than stalling a user's request.
    /// </remarks>
    public TelemetryOverflowPolicy OverflowPolicy { get; set; } = TelemetryOverflowPolicy.DropNewest;

    /// <summary>
    /// Timeout for a single attempt against Langfuse. Retries each get their own budget; the
    /// resilience pipeline's total budget is derived from this (4x), so a slow first attempt cannot
    /// consume the retries' time.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Behaviour when the telemetry queue is full.</summary>
public enum TelemetryOverflowPolicy
{
    /// <summary>Discard the incoming event, keeping the existing backlog.</summary>
    DropNewest,

    /// <summary>
    /// Discard the oldest queued event to make room for the newest. Preferable while debugging a
    /// live incident, where the most recent events are the informative ones.
    /// </summary>
    DropOldest,
}
