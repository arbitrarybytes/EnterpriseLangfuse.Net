using EnterpriseLangfuse.Api;

namespace EnterpriseLangfuse.Telemetry;

/// <summary>Maps the public telemetry model onto Langfuse wire bodies.</summary>
/// <remarks>
/// Applies the ambient <see cref="LangfuseOptions.Environment"/> and
/// <see cref="LangfuseOptions.Release"/> here rather than at each call site, so every event carries
/// them without callers having to remember.
/// </remarks>
internal static class TelemetryMapper
{
    public static TraceBodyDto ToBody(LangfuseTrace trace, LangfuseOptions options) => new()
    {
        Id = trace.Id,
        Name = trace.Name,
        Timestamp = trace.Timestamp,
        UserId = trace.UserId,
        SessionId = trace.SessionId,
        Input = trace.Input,
        Output = trace.Output,
        Metadata = trace.Metadata,
        Tags = trace.Tags?.ToList(),
        Environment = options.Environment,
        Release = options.Release,
    };

    public static GenerationBodyDto ToBody(LangfuseGeneration generation, LangfuseOptions options) => new()
    {
        Id = generation.Id,
        TraceId = generation.TraceId,
        ParentObservationId = generation.ParentObservationId,
        Name = generation.Name,
        StartTime = generation.StartTime,
        EndTime = generation.EndTime,
        CompletionStartTime = generation.CompletionStartTime,
        Model = generation.Model,
        ModelParameters = generation.ModelParameters,
        Input = generation.Input,
        Output = generation.Output,
        Metadata = generation.Metadata,
        UsageDetails = generation.Usage is null ? null : new Dictionary<string, long>(generation.Usage),
        Level = ToWireLevel(generation.Level),
        StatusMessage = generation.StatusMessage,
        PromptName = generation.PromptName,
        PromptVersion = generation.PromptVersion,
        Environment = options.Environment,
    };

    public static SpanBodyDto ToBody(LangfuseSpan span, LangfuseOptions options) => new()
    {
        Id = span.Id,
        TraceId = span.TraceId,
        ParentObservationId = span.ParentObservationId,
        Name = span.Name,
        StartTime = span.StartTime,
        EndTime = span.EndTime,
        Input = span.Input,
        Output = span.Output,
        Metadata = span.Metadata,
        Level = ToWireLevel(span.Level),
        StatusMessage = span.StatusMessage,
        Environment = options.Environment,
    };

    public static ScoreBodyDto ToBody(LangfuseScore score, LangfuseOptions options) => new()
    {
        Id = score.Id,
        TraceId = score.TraceId,
        ObservationId = score.ObservationId,
        Name = score.Name,
        Value = score.Value,
        DataType = score.DataType,
        Comment = score.Comment,
        Environment = options.Environment,
    };

    /// <summary>Langfuse expects these levels upper-cased on the wire.</summary>
    private static string ToWireLevel(ObservationLevel level) => level switch
    {
        ObservationLevel.Debug => "DEBUG",
        ObservationLevel.Warning => "WARNING",
        ObservationLevel.Error => "ERROR",
        _ => "DEFAULT",
    };
}
