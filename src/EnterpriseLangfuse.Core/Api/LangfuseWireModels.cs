using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EnterpriseLangfuse.Api;

/// <summary>
/// Wire contracts for the Langfuse public API.
/// </summary>
/// <remarks>
/// These exist because the AutoSDK's generated models cannot round-trip these payloads: its
/// <c>AllOf&lt;T1,T2&gt;</c> converter emits only the discriminator and drops the body, so an
/// ingestion event serialises to <c>{"type":"trace-create"}</c> and a fetched prompt comes back with a
/// null body and a mis-resolved text/chat discriminator. The converter is registered inside the
/// AutoSDK's baked source-generation context and cannot be replaced from outside, so these paths own
/// their own contracts. The AutoSDK is still used for transport, auth, and prompt creation, where it
/// is correct.
/// <para>
/// Free-form fields are typed as <see cref="JsonNode"/> rather than <c>object</c>: it carries
/// arbitrary JSON without the reflection-based polymorphic serialisation that would break Native AOT.
/// </para>
/// </remarks>
internal static class IngestionEventTypes
{
    public const string TraceCreate = "trace-create";
    public const string GenerationCreate = "generation-create";
    public const string GenerationUpdate = "generation-update";
    public const string SpanCreate = "span-create";
    public const string SpanUpdate = "span-update";
    public const string EventCreate = "event-create";
    public const string ScoreCreate = "score-create";
}

/// <summary>A prompt as returned by <c>GET /api/public/v2/prompts/{name}</c>.</summary>
internal sealed class PromptResponseDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Either <c>text</c> or <c>chat</c>. Absent on older payloads, which implies text.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>A string for text prompts, or an array of messages for chat prompts.</summary>
    [JsonPropertyName("prompt")]
    public JsonNode? Prompt { get; set; }

    [JsonPropertyName("config")]
    public JsonNode? Config { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; set; }
}

/// <summary>A chat message inside a chat prompt's <c>prompt</c> array.</summary>
internal sealed class PromptMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>The envelope for <c>POST /api/public/ingestion</c>.</summary>
internal sealed class IngestionRequestDto
{
    [JsonPropertyName("batch")]
    public List<IngestionEventDto> Batch { get; set; } = [];
}

/// <summary>
/// One event in an ingestion batch. <see cref="Body"/> is a pre-serialised <see cref="JsonNode"/> so a
/// heterogeneous batch stays strongly typed at each call site without polymorphic serialisation.
/// </summary>
internal sealed class IngestionEventDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("body")]
    public JsonNode? Body { get; set; }
}

/// <summary>Langfuse's per-event result. A batch can partially succeed, so failures are per-event.</summary>
internal sealed class IngestionResponseDto
{
    [JsonPropertyName("successes")]
    public List<IngestionSuccessDto>? Successes { get; set; }

    [JsonPropertyName("errors")]
    public List<IngestionErrorDto>? Errors { get; set; }
}

internal sealed class IngestionSuccessDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

internal sealed class IngestionErrorDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public JsonNode? Error { get; set; }
}

/// <summary>Body of a <c>trace-create</c> event.</summary>
internal sealed class TraceBodyDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("input")]
    public JsonNode? Input { get; set; }

    [JsonPropertyName("output")]
    public JsonNode? Output { get; set; }

    [JsonPropertyName("metadata")]
    public JsonNode? Metadata { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Body of a <c>generation-create</c>/<c>generation-update</c> event — an LLM call, the observation
/// type that carries model, token usage and cost.
/// </summary>
internal sealed class GenerationBodyDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("parentObservationId")]
    public string? ParentObservationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>When the first token arrived — Langfuse derives time-to-first-token from this.</summary>
    [JsonPropertyName("completionStartTime")]
    public DateTimeOffset? CompletionStartTime { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("modelParameters")]
    public JsonNode? ModelParameters { get; set; }

    [JsonPropertyName("input")]
    public JsonNode? Input { get; set; }

    [JsonPropertyName("output")]
    public JsonNode? Output { get; set; }

    [JsonPropertyName("metadata")]
    public JsonNode? Metadata { get; set; }

    /// <summary>Token counts keyed by kind (<c>input</c>, <c>output</c>, <c>total</c>, ...).</summary>
    [JsonPropertyName("usageDetails")]
    public Dictionary<string, long>? UsageDetails { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; set; }

    /// <summary>Links this generation to the prompt revision that produced it.</summary>
    [JsonPropertyName("promptName")]
    public string? PromptName { get; set; }

    [JsonPropertyName("promptVersion")]
    public int? PromptVersion { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

/// <summary>Body of a <c>span-create</c>/<c>span-update</c> event — a non-LLM unit of work.</summary>
internal sealed class SpanBodyDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("parentObservationId")]
    public string? ParentObservationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTimeOffset? EndTime { get; set; }

    [JsonPropertyName("input")]
    public JsonNode? Input { get; set; }

    [JsonPropertyName("output")]
    public JsonNode? Output { get; set; }

    [JsonPropertyName("metadata")]
    public JsonNode? Metadata { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

/// <summary>Body of a <c>score-create</c> event — an evaluation attached to a trace or observation.</summary>
internal sealed class ScoreBodyDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("observationId")]
    public string? ObservationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public JsonNode? Value { get; set; }

    [JsonPropertyName("dataType")]
    public string? DataType { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}
