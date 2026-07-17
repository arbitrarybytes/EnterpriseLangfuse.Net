using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.Logging;

namespace EnterpriseLangfuse.Api;

/// <summary>Result of dispatching one ingestion batch.</summary>
/// <param name="SuccessCount">Events Langfuse accepted.</param>
/// <param name="Errors">Per-event rejections. Langfuse accepts batches partially.</param>
internal sealed record IngestionResult(int SuccessCount, IReadOnlyList<IngestionErrorDto> Errors)
{
    public static readonly IngestionResult Empty = new(0, []);
}

/// <summary>
/// The Langfuse endpoints this framework talks to directly, behind an interface so the resilience
/// pipeline and the background dispatcher can be tested without HTTP.
/// </summary>
internal interface ILangfuseApi
{
    /// <summary>
    /// Fetches a prompt.
    /// </summary>
    /// <returns>The prompt, or null when Langfuse reports it does not exist (HTTP 404).</returns>
    /// <exception cref="HttpRequestException">The request failed transiently (5xx, timeout, DNS).</exception>
    /// <exception cref="LangfuseApiException">Langfuse responded, but not with a prompt we can read.</exception>
    Task<LangfusePrompt?> GetPromptAsync(string name, string? label, CancellationToken cancellationToken);

    /// <summary>Dispatches a batch of telemetry events.</summary>
    Task<IngestionResult> IngestAsync(IReadOnlyList<IngestionEventDto> events, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ILangfuseApi"/> over <see cref="HttpClient"/>, using this library's source-generated
/// JSON contracts rather than the AutoSDK's models (see LangfuseWireModels.cs for why).
/// </summary>
internal sealed class LangfuseApi : ILangfuseApi
{
    private const string PromptPath = "api/public/v2/prompts/";
    private const string IngestionPath = "api/public/ingestion";

    private readonly HttpClient _httpClient;
    private readonly ILogger<LangfuseApi> _logger;

    public LangfuseApi(HttpClient httpClient, ILogger<LangfuseApi> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<LangfusePrompt?> GetPromptAsync(string name, string? label, CancellationToken cancellationToken)
    {
        var uri = BuildPromptUri(name, label);

        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // Surfaces 5xx as HttpRequestException, which is precisely the signal the L3 fallback catches.
        response.EnsureSuccessStatusCode();

        PromptResponseDto? dto;
        try
        {
            dto = await response.Content
                .ReadFromJsonAsync(LangfuseJsonContext.Default.PromptResponseDto, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new LangfuseApiException(
                $"Langfuse returned a prompt payload for '{name}' that could not be parsed.",
                response.StatusCode,
                ex);
        }

        if (dto is null)
        {
            throw new LangfuseApiException($"Langfuse returned an empty body for prompt '{name}'.", response.StatusCode);
        }

        return PromptResponseMapper.ToPrompt(dto, name, PromptSource.Network);
    }

    public async Task<IngestionResult> IngestAsync(
        IReadOnlyList<IngestionEventDto> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return IngestionResult.Empty;
        }

        var request = new IngestionRequestDto { Batch = [.. events] };

        using var response = await _httpClient
            .PostAsJsonAsync(IngestionPath, request, LangfuseJsonContext.Default.IngestionRequestDto, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        IngestionResponseDto? body;
        try
        {
            body = await response.Content
                .ReadFromJsonAsync(LangfuseJsonContext.Default.IngestionResponseDto, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // The server already accepted the batch (success status); an unreadable *response* must
            // not be reported as a send failure, or the dispatcher would count delivered events as
            // dropped and a retry would duplicate them.
            _logger.IngestionResponseUnreadable(events.Count, ex);
            return new IngestionResult(events.Count, []);
        }

        var errors = (IReadOnlyList<IngestionErrorDto>?)body?.Errors ?? [];
        var successes = body?.Successes?.Count ?? 0;

        // A 207-style partial failure must not fail the batch: the accepted events are already stored,
        // and retrying the whole batch would duplicate them.
        foreach (var error in errors)
        {
            _logger.IngestionEventRejected(error.Id ?? "(unknown)", error.Status, error.Message ?? "(no message)");
        }

        return new IngestionResult(successes, errors);
    }

    /// <summary>
    /// Builds the prompt URI. The name is path-escaped because prompt names routinely contain
    /// slashes and spaces (e.g. <c>support/refund agent</c>).
    /// </summary>
    private static string BuildPromptUri(string name, string? label)
    {
        var uri = PromptPath + Uri.EscapeDataString(name);
        return string.IsNullOrEmpty(label) ? uri : $"{uri}?label={Uri.EscapeDataString(label)}";
    }
}

/// <summary>Maps a wire prompt onto the domain model.</summary>
internal static class PromptResponseMapper
{
    public static LangfusePrompt ToPrompt(PromptResponseDto dto, string requestedName, PromptSource source)
    {
        var name = dto.Name ?? requestedName;
        var isChat = ResolveIsChat(dto);

        return isChat
            ? new LangfusePrompt(
                name,
                dto.Version,
                LangfusePromptType.Chat,
                text: null,
                messages: ReadMessages(dto.Prompt, name),
                labels: dto.Labels ?? [],
                tags: dto.Tags ?? [],
                config: ReadConfig(dto.Config),
                source)
            : new LangfusePrompt(
                name,
                dto.Version,
                LangfusePromptType.Text,
                text: ReadText(dto.Prompt, name),
                messages: [],
                labels: dto.Labels ?? [],
                tags: dto.Tags ?? [],
                config: ReadConfig(dto.Config),
                source);
    }

    /// <summary>
    /// Decides text vs chat. The declared <c>type</c> wins; when absent, the body's JSON shape decides
    /// (an array means chat). Older Langfuse payloads omit <c>type</c> entirely.
    /// </summary>
    private static bool ResolveIsChat(PromptResponseDto dto)
    {
        if (string.Equals(dto.Type, "chat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(dto.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return dto.Prompt is JsonArray;
    }

    private static string ReadText(JsonNode? node, string name) => node switch
    {
        null => throw new LangfuseApiException($"Prompt '{name}' has no body."),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new LangfuseApiException($"Prompt '{name}' is typed 'text' but its body is not a string."),
    };

    private static IReadOnlyList<LangfuseChatMessage> ReadMessages(JsonNode? node, string name)
    {
        if (node is not JsonArray array)
        {
            throw new LangfuseApiException($"Prompt '{name}' is typed 'chat' but its body is not an array of messages.");
        }

        var messages = new List<LangfuseChatMessage>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject message)
            {
                throw new LangfuseApiException($"Prompt '{name}' contains a message that is not an object.");
            }

            // Placeholder messages (Langfuse's message-list feature) have no literal content; skipping
            // them keeps a prompt that uses placeholders usable rather than failing the whole fetch.
            // TryGetValue rather than GetValue: a non-string role/content must degrade the same way,
            // not escape as an InvalidOperationException that bypasses the fallback tier.
            var role = message["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var r) ? r : null;
            var content = message["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var c) ? c : null;
            if (role is null || content is null)
            {
                continue;
            }

            messages.Add(new LangfuseChatMessage(role, content));
        }

        return messages;
    }

    private static IReadOnlyDictionary<string, object?> ReadConfig(JsonNode? node)
    {
        if (node is not JsonObject config)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>(config.Count, StringComparer.Ordinal);
        foreach (var (key, value) in config)
        {
            result[key] = ToClrValue(value);
        }

        return result;
    }

    private static object? ToClrValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => ToScalar(value),
        _ => node.ToJsonString(),
    };

    private static object? ToScalar(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        return value.ToJsonString();
    }
}
