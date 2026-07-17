using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EnterpriseLangfuse.Api;

/// <summary>
/// Source-generated serialisation metadata for every Langfuse payload this library reads or writes.
/// </summary>
/// <remarks>
/// Source generation is what makes <c>IsAotCompatible</c> honest: no reflection-based contract
/// discovery, so the whole serialisation path survives trimming and Native AOT.
/// <para>
/// Nulls are omitted because Langfuse treats an absent field and an explicit null differently on
/// update events — sending <c>"output": null</c> in a <c>generation-update</c> would clear a value the
/// create event had already set.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PromptResponseDto))]
[JsonSerializable(typeof(PromptMessageDto))]
[JsonSerializable(typeof(List<PromptMessageDto>))]
[JsonSerializable(typeof(IngestionRequestDto))]
[JsonSerializable(typeof(IngestionResponseDto))]
[JsonSerializable(typeof(IngestionEventDto))]
[JsonSerializable(typeof(TraceBodyDto))]
[JsonSerializable(typeof(GenerationBodyDto))]
[JsonSerializable(typeof(SpanBodyDto))]
[JsonSerializable(typeof(ScoreBodyDto))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class LangfuseJsonContext : JsonSerializerContext;
