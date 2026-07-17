using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>Bridges a <see cref="CompiledPrompt"/> into MEAI's message model.</summary>
public static class CompiledPromptExtensions
{
    /// <summary>
    /// Converts a compiled prompt into MEAI chat messages.
    /// </summary>
    /// <remarks>
    /// A text prompt becomes a single user message, which is the conventional reading of an
    /// unstructured prompt body. Unrecognised roles are mapped to <see cref="ChatRole.User"/> rather
    /// than rejected, so a prompt authored with a custom role in Langfuse still sends.
    /// </remarks>
    public static IList<ChatMessage> ToChatMessages(this CompiledPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (prompt.Type == LangfusePromptType.Text)
        {
            return [new ChatMessage(ChatRole.User, prompt.Text ?? string.Empty)];
        }

        var messages = new List<ChatMessage>(prompt.Messages.Count);
        foreach (var message in prompt.Messages)
        {
            messages.Add(new ChatMessage(ToRole(message.Role), message.Content));
        }

        return messages;
    }

    private static ChatRole ToRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };
}
