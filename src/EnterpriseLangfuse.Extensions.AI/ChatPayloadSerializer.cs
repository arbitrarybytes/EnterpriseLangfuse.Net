using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>
/// Projects MEAI types onto the JSON shape Langfuse's UI renders natively.
/// </summary>
/// <remarks>
/// Built with <see cref="JsonNode"/> by hand rather than by serialising MEAI's own types. Two
/// reasons: MEAI content types are polymorphic, and reflection-based polymorphic serialisation would
/// break Native AOT; and Langfuse renders a chat trace properly only when messages arrive as a plain
/// <c>[{role, content}]</c> array, which is not MEAI's wire shape.
/// </remarks>
internal static class ChatPayloadSerializer
{
    public static JsonNode? SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var message in messages)
        {
            var entry = new JsonObject
            {
                ["role"] = JsonValue.Create(message.Role.Value),
                ["content"] = JsonValue.Create(message.Text),
            };

            if (message.AuthorName is { Length: > 0 } author)
            {
                entry["name"] = JsonValue.Create(author);
            }

            // Tool calls are the part of an agent trace people actually debug, so surface them
            // rather than letting them vanish into an empty content string.
            if (CollectToolCalls(message) is { } calls)
            {
                entry["tool_calls"] = calls;
            }

            array.Add((JsonNode)entry);
        }

        return array;
    }

    public static JsonNode? SerializeResponse(ChatResponse response)
    {
        var array = new JsonArray();
        foreach (var message in response.Messages)
        {
            var entry = new JsonObject
            {
                ["role"] = JsonValue.Create(message.Role.Value),
                ["content"] = JsonValue.Create(message.Text),
            };

            if (CollectToolCalls(message) is { } calls)
            {
                entry["tool_calls"] = calls;
            }

            array.Add((JsonNode)entry);
        }

        return array.Count switch
        {
            0 => null,
            // A single assistant message renders more cleanly as the message itself than as a
            // one-element array.
            1 => array[0]!.DeepClone(),
            _ => array,
        };
    }

    /// <summary>
    /// Serialises a streamed result, preferring the text accumulated across updates.
    /// </summary>
    /// <param name="response">The updates coalesced back into a response.</param>
    /// <param name="streamedText">Text accumulated chunk by chunk as it was yielded.</param>
    public static JsonNode? SerializeStreamedOutput(ChatResponse response, string streamedText)
    {
        var serialized = SerializeResponse(response);

        // Prefer the reconstructed messages when they carry structure (tool calls); fall back to raw
        // accumulated text when coalescing produced nothing useful.
        if (serialized is not null && !IsEmptyContent(serialized))
        {
            return serialized;
        }

        return streamedText.Length > 0
            ? new JsonObject { ["role"] = JsonValue.Create("assistant"), ["content"] = JsonValue.Create(streamedText) }
            : null;
    }

    /// <summary>Maps MEAI usage onto Langfuse's <c>usageDetails</c> keys.</summary>
    public static Dictionary<string, long>? ToUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        if (usage.InputTokenCount is { } input)
        {
            result["input"] = input;
        }

        if (usage.OutputTokenCount is { } output)
        {
            result["output"] = output;
        }

        if (usage.TotalTokenCount is { } total)
        {
            result["total"] = total;
        }

        // Cached and reasoning tokens are billed differently; Langfuse prices them separately when
        // the keys are present, so dropping them would misstate cost.
        if (usage.CachedInputTokenCount is { } cached)
        {
            result["input_cached"] = cached;
        }

        if (usage.ReasoningTokenCount is { } reasoning)
        {
            result["output_reasoning"] = reasoning;
        }

        if (usage.AdditionalCounts is { Count: > 0 } additional)
        {
            foreach (var (key, value) in additional)
            {
                result.TryAdd(key, value);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonArray? CollectToolCalls(ChatMessage message)
    {
        JsonArray? calls = null;

        foreach (var content in message.Contents)
        {
            if (content is not FunctionCallContent call)
            {
                continue;
            }

            calls ??= [];
            calls.Add((JsonNode)new JsonObject
            {
                ["id"] = JsonValue.Create(call.CallId),
                ["name"] = JsonValue.Create(call.Name),
            });
        }

        return calls;
    }

    private static bool IsEmptyContent(JsonNode node) => node switch
    {
        JsonObject obj => IsBlank(obj["content"]) && obj["tool_calls"] is null,
        JsonArray array => array.Count == 0,
        _ => false,
    };

    private static bool IsBlank(JsonNode? node) =>
        node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrEmpty(text));
}
