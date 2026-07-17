using System.Text.Json.Nodes;

namespace EnterpriseLangfuse.Telemetry;

/// <summary>Severity of an observation, mirroring Langfuse's levels.</summary>
public enum ObservationLevel
{
    /// <summary>Diagnostic detail.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Default,

    /// <summary>Completed, but something was off.</summary>
    Warning,

    /// <summary>Failed. Surfaces in Langfuse's error views.</summary>
    Error,
}

/// <summary>A trace: one end-to-end request through the application.</summary>
public sealed class LangfuseTrace
{
    /// <summary>Trace id. Defaults to a new id; set it to the W3C trace id to correlate with OpenTelemetry.</summary>
    public string Id { get; set; } = TraceIdentifier.New();

    /// <summary>Display name, e.g. the endpoint or workflow that was invoked.</summary>
    public string? Name { get; set; }

    /// <summary>When the trace started. Defaults to ingestion time when unset.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>The end user this request belongs to; groups traces per user in Langfuse.</summary>
    public string? UserId { get; set; }

    /// <summary>Groups related traces into a conversation or session.</summary>
    public string? SessionId { get; set; }

    /// <summary>The request input.</summary>
    public JsonNode? Input { get; set; }

    /// <summary>The final output returned to the user.</summary>
    public JsonNode? Output { get; set; }

    /// <summary>Arbitrary structured metadata.</summary>
    public JsonNode? Metadata { get; set; }

    /// <summary>Free-form tags for filtering in Langfuse.</summary>
    public IList<string>? Tags { get; set; }
}

/// <summary>A generation: a single LLM call, with model, tokens and timings.</summary>
public sealed class LangfuseGeneration
{
    /// <summary>Observation id.</summary>
    public string Id { get; set; } = TraceIdentifier.New();

    /// <summary>The trace this generation belongs to.</summary>
    public string? TraceId { get; set; }

    /// <summary>The enclosing observation, if this call is nested inside a span.</summary>
    public string? ParentObservationId { get; set; }

    /// <summary>Display name for the call.</summary>
    public string? Name { get; set; }

    /// <summary>When the request was issued.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>When the response completed.</summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>When the first token arrived; Langfuse derives time-to-first-token from this.</summary>
    public DateTimeOffset? CompletionStartTime { get; set; }

    /// <summary>Model identifier; Langfuse prices the call from this.</summary>
    public string? Model { get; set; }

    /// <summary>Model settings such as temperature and max tokens.</summary>
    public JsonNode? ModelParameters { get; set; }

    /// <summary>The messages sent to the model.</summary>
    public JsonNode? Input { get; set; }

    /// <summary>The model's response.</summary>
    public JsonNode? Output { get; set; }

    /// <summary>Arbitrary structured metadata.</summary>
    public JsonNode? Metadata { get; set; }

    /// <summary>Token counts keyed by kind: <c>input</c>, <c>output</c>, <c>total</c>, ...</summary>
    public IDictionary<string, long>? Usage { get; set; }

    /// <summary>Severity; set to <see cref="ObservationLevel.Error"/> when the call failed.</summary>
    public ObservationLevel Level { get; set; } = ObservationLevel.Default;

    /// <summary>Error text or status detail accompanying <see cref="Level"/>.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Name of the prompt that produced this generation, linking it to a prompt revision.</summary>
    public string? PromptName { get; set; }

    /// <summary>Version of the prompt that produced this generation.</summary>
    public int? PromptVersion { get; set; }
}

/// <summary>A span: a unit of work that is not an LLM call (retrieval, tool call, ...).</summary>
public sealed class LangfuseSpan
{
    /// <summary>Observation id.</summary>
    public string Id { get; set; } = TraceIdentifier.New();

    /// <summary>The trace this span belongs to.</summary>
    public string? TraceId { get; set; }

    /// <summary>The enclosing observation, if this span is nested.</summary>
    public string? ParentObservationId { get; set; }

    /// <summary>Display name for the unit of work.</summary>
    public string? Name { get; set; }

    /// <summary>When the work started.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>When the work finished.</summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>The span's input.</summary>
    public JsonNode? Input { get; set; }

    /// <summary>The span's output.</summary>
    public JsonNode? Output { get; set; }

    /// <summary>Arbitrary structured metadata.</summary>
    public JsonNode? Metadata { get; set; }

    /// <summary>Severity; set to <see cref="ObservationLevel.Error"/> when the work failed.</summary>
    public ObservationLevel Level { get; set; } = ObservationLevel.Default;

    /// <summary>Error text or status detail accompanying <see cref="Level"/>.</summary>
    public string? StatusMessage { get; set; }
}

/// <summary>A score: an evaluation attached to a trace or observation.</summary>
public sealed class LangfuseScore
{
    /// <summary>Score id.</summary>
    public string Id { get; set; } = TraceIdentifier.New();

    /// <summary>The trace being scored.</summary>
    public string? TraceId { get; set; }

    /// <summary>The specific observation being scored, if narrower than the trace.</summary>
    public string? ObservationId { get; set; }

    /// <summary>The score's name, e.g. <c>helpfulness</c>. Groups scores in Langfuse.</summary>
    public required string Name { get; set; }

    /// <summary>Numeric value, or a category label for categorical scores.</summary>
    public JsonNode? Value { get; set; }

    /// <summary><c>NUMERIC</c>, <c>CATEGORICAL</c> or <c>BOOLEAN</c>.</summary>
    public string? DataType { get; set; }

    /// <summary>Free-text rationale for the score.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Records LLM telemetry without blocking the caller.
/// </summary>
/// <remarks>
/// Every method here is non-blocking by contract: the event is handed to an in-memory channel and
/// dispatched by a background service. Creating a span must never add network latency to an
/// application request, and must never fail one either — if Langfuse is unreachable or the queue is
/// saturated, telemetry is dropped and counted, not thrown.
/// </remarks>
public interface ILangfuseTelemetry
{
    /// <summary>Queues a trace. Returns false if the event was dropped because the queue was full.</summary>
    bool Track(LangfuseTrace trace);

    /// <summary>Queues a generation (LLM call).</summary>
    bool Track(LangfuseGeneration generation);

    /// <summary>Queues a span.</summary>
    bool Track(LangfuseSpan span);

    /// <summary>Queues a score.</summary>
    bool Track(LangfuseScore score);

    /// <summary>
    /// Waits until everything queued so far has been dispatched. Intended for tests and for
    /// short-lived processes (a CLI or job) that would otherwise exit before the background service
    /// flushes. Long-running applications should not call this on a request path.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Generates Langfuse-compatible identifiers.</summary>
public static class TraceIdentifier
{
    /// <summary>A new opaque id.</summary>
    public static string New() => Guid.NewGuid().ToString("n");
}
