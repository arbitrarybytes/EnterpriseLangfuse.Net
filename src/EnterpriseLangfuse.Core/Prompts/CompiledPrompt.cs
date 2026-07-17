namespace EnterpriseLangfuse.Prompts;

/// <summary>
/// A prompt with every <c>{{variable}}</c> substituted — ready to send to a model.
/// </summary>
/// <remarks>
/// Carries <see cref="Name"/>, <see cref="Version"/> and <see cref="Source"/> alongside the rendered
/// body so downstream tracing can link a generation back to the exact prompt revision that produced
/// it, which is what makes prompt-level evaluation in Langfuse possible.
/// </remarks>
public sealed class CompiledPrompt
{
    internal CompiledPrompt(
        string name,
        int version,
        LangfusePromptType type,
        string? text,
        IReadOnlyList<LangfuseChatMessage> messages,
        PromptSource source)
    {
        Name = name;
        Version = version;
        Type = type;
        Text = text;
        Messages = messages;
        Source = source;
    }

    /// <summary>The prompt's name in Langfuse.</summary>
    public string Name { get; }

    /// <summary>The resolved version; zero when served from an embedded fallback.</summary>
    public int Version { get; }

    /// <summary>Whether this is a text or a chat prompt.</summary>
    public LangfusePromptType Type { get; }

    /// <summary>The rendered body of a text prompt; null for chat prompts.</summary>
    public string? Text { get; }

    /// <summary>The rendered messages of a chat prompt; empty for text prompts.</summary>
    public IReadOnlyList<LangfuseChatMessage> Messages { get; }

    /// <summary>Where the underlying prompt was served from.</summary>
    public PromptSource Source { get; }

    /// <summary>
    /// True when this prompt was served from an embedded fallback rather than Langfuse — i.e. the
    /// application is running degraded. Useful as a metric or alarm condition.
    /// </summary>
    public bool IsFallback => Source == PromptSource.EmbeddedFallback;

    /// <summary>The rendered text, or the chat messages flattened to <c>role: content</c> lines.</summary>
    public override string ToString() =>
        Type == LangfusePromptType.Text
            ? Text ?? string.Empty
            : string.Join(Environment.NewLine, Messages.Select(m => $"{m.Role}: {m.Content}"));
}
