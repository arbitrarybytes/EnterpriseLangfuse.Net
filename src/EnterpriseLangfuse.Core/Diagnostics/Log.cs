using Microsoft.Extensions.Logging;

namespace EnterpriseLangfuse;

/// <summary>
/// Source-generated log methods.
/// </summary>
/// <remarks>
/// <see cref="LoggerMessageAttribute"/> rather than <c>ILogger.LogWarning(...)</c> so that disabled
/// log levels cost nothing (no boxing, no string formatting) on paths that run per LLM call, and so
/// the logging path stays reflection-free under Native AOT.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Langfuse is unreachable; serving prompt '{PromptName}' from the embedded fallback. The application is running on a possibly stale prompt.")]
    public static partial void ServingEmbeddedFallback(this ILogger logger, string promptName, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "Resolved prompt '{PromptName}' (version {Version}) from {Source}.")]
    public static partial void PromptResolved(this ILogger logger, string promptName, int version, string source);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Langfuse rejected telemetry event '{EventId}' with status {Status}: {Message}")]
    public static partial void IngestionEventRejected(this ILogger logger, string eventId, int status, string message);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Failed to dispatch a telemetry batch of {Count} event(s). The batch was dropped.")]
    public static partial void IngestionBatchFailed(this ILogger logger, int count, Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Telemetry queue is full ({Capacity}); dropped a '{EventType}' event. Raise TelemetryQueueCapacity or check Langfuse connectivity.")]
    public static partial void TelemetryEventDropped(this ILogger logger, int capacity, string eventType);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Flushed {Count} telemetry event(s) to Langfuse.")]
    public static partial void TelemetryBatchFlushed(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Warning,
        Message = "Shutdown drain timed out; {Count} telemetry event(s) were not delivered.")]
    public static partial void ShutdownDrainTimedOut(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Warning,
        Message = "Langfuse accepted a batch of {Count} event(s) but returned an unreadable response body; treating the batch as delivered.")]
    public static partial void IngestionResponseUnreadable(this ILogger logger, int count, Exception exception);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Debug,
        Message = "Loaded {Count} embedded prompt fallback(s) from {Assembly}.")]
    public static partial void EmbeddedFallbacksLoaded(this ILogger logger, int count, string assembly);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Error,
        Message = "Embedded prompt resource '{ResourceName}' is not a valid prompt file and was ignored.")]
    public static partial void EmbeddedFallbackInvalid(this ILogger logger, string resourceName, Exception exception);
}
