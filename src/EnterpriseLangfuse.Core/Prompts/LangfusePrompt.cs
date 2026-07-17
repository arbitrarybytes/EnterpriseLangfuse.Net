using System.Diagnostics.CodeAnalysis;
using System.Text;
using EnterpriseLangfuse.Shared;

namespace EnterpriseLangfuse.Prompts;

/// <summary>Whether a prompt is a single text body or an ordered list of chat messages.</summary>
public enum LangfusePromptType
{
    /// <summary>A single text body.</summary>
    Text,

    /// <summary>An ordered list of role/content chat messages.</summary>
    Chat,
}

/// <summary>
/// Where a prompt's content originated. Surfaced so callers can alert on degraded serving.
/// </summary>
/// <remarks>
/// This describes the <em>origin of the content</em>, not which tier answered the call, and it is
/// preserved across caching on purpose: a fallback that has been cached is still a fallback, and
/// re-tagging it as "cache" on the way out would hide exactly the degradation worth alarming on.
/// Cache hit rate is reported as a metric instead — see <c>LangfuseMetrics</c>.
/// </remarks>
public enum PromptSource
{
    /// <summary>Fetched from the Langfuse API.</summary>
    Network,

    /// <summary>Served from an embedded <c>.prompt.yaml</c> because Langfuse could not answer.</summary>
    EmbeddedFallback,
}

/// <summary>A single chat message within a chat prompt.</summary>
/// <param name="Role">The message role, e.g. <c>system</c>, <c>user</c> or <c>assistant</c>.</param>
/// <param name="Content">The message body, which may contain <c>{{mustache}}</c> variables.</param>
public sealed record LangfuseChatMessage(string Role, string Content);

/// <summary>
/// A prompt resolved from Langfuse (or an offline fallback), in a shape this framework controls.
/// </summary>
/// <remarks>
/// This is deliberately a domain type rather than a re-export of the AutoSDK's <c>Prompt</c> union.
/// The AutoSDK models prompts as an <c>AllOf</c> discriminated union whose converter cannot round-trip
/// a body, so a re-export would leak an unusable type into this library's public API. Mapping into
/// this type at the boundary keeps that defect from reaching callers.
/// <para>
/// Templates are parsed into segments once, at construction, so that
/// <see cref="Compile(IReadOnlyDictionary{string, object?})"/> — which runs on every LLM call — is a
/// pure render with no parsing.
/// </para>
/// </remarks>
public sealed class LangfusePrompt
{
    private readonly MustacheSegment[] _textSegments;
    private readonly MustacheSegment[][] _messageSegments;

    internal LangfusePrompt(
        string name,
        int version,
        LangfusePromptType type,
        string? text,
        IReadOnlyList<LangfuseChatMessage> messages,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, object?> config,
        PromptSource source)
    {
        Name = name;
        Version = version;
        Type = type;
        Text = text;
        Messages = messages;
        Labels = labels;
        Tags = tags;
        Config = config;
        Source = source;

        _textSegments = type == LangfusePromptType.Text
            ? MustacheParser.Parse(text).ToArray()
            : [];

        _messageSegments = type == LangfusePromptType.Chat
            ? [.. messages.Select(m => MustacheParser.Parse(m.Content).ToArray())]
            : [];

        Variables = CollectVariables(_textSegments, _messageSegments);
    }

    /// <summary>The prompt's name in Langfuse.</summary>
    public string Name { get; }

    /// <summary>
    /// The resolved version. Zero when served from an embedded fallback, since a local file has no
    /// server-assigned version — check <see cref="Source"/> before treating this as authoritative.
    /// </summary>
    public int Version { get; }

    /// <summary>Whether this is a text or a chat prompt.</summary>
    public LangfusePromptType Type { get; }

    /// <summary>The body of a <see cref="LangfusePromptType.Text"/> prompt; null for chat prompts.</summary>
    public string? Text { get; }

    /// <summary>The messages of a <see cref="LangfusePromptType.Chat"/> prompt; empty for text prompts.</summary>
    public IReadOnlyList<LangfuseChatMessage> Messages { get; }

    /// <summary>Labels attached to this prompt version in Langfuse, e.g. <c>production</c>.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Tags attached to the prompt in Langfuse.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Model configuration attached to the prompt in Langfuse (model name, temperature, ...).
    /// Scalars keep their natural CLR types; nested objects and arrays are exposed as their raw JSON
    /// text, since a dictionary of <see cref="object"/> cannot carry structure without inviting
    /// reflection-based serialisation that would break AOT.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Config { get; }

    /// <summary>Where this instance was served from.</summary>
    public PromptSource Source { get; }

    /// <summary>Every distinct <c>{{mustache}}</c> variable in the body, in first-seen order.</summary>
    public IReadOnlyList<string> Variables { get; }

    /// <summary>
    /// Renders the prompt, substituting every <c>{{variable}}</c> with its value.
    /// </summary>
    /// <param name="variables">Values to bind. Keys are matched ordinally.</param>
    /// <exception cref="MissingPromptVariableException">
    /// A variable referenced by the template has no value supplied.
    /// </exception>
    /// <remarks>
    /// Missing variables throw rather than rendering as empty or leaving the raw <c>{{tag}}</c> in
    /// place. Silently shipping a half-rendered prompt to an LLM is a production incident that is
    /// hard to trace back; failing here surfaces it at the call site instead.
    /// </remarks>
    public CompiledPrompt Compile(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (Type == LangfusePromptType.Text)
        {
            return new CompiledPrompt(Name, Version, Type, Render(_textSegments, variables), [], Source);
        }

        var rendered = new LangfuseChatMessage[Messages.Count];
        for (var i = 0; i < Messages.Count; i++)
        {
            rendered[i] = new LangfuseChatMessage(Messages[i].Role, Render(_messageSegments[i], variables));
        }

        return new CompiledPrompt(Name, Version, Type, null, rendered, Source);
    }

    /// <inheritdoc cref="Compile(IReadOnlyDictionary{string, object?})"/>
    public CompiledPrompt Compile() => Compile(EmptyVariables);

    private static readonly Dictionary<string, object?> EmptyVariables = [];

    private string Render(MustacheSegment[] segments, IReadOnlyDictionary<string, object?> variables)
    {
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        // Single literal segment (a prompt with no variables) needs no building at all.
        if (segments.Length == 1 && !segments[0].IsVariable)
        {
            return segments[0].Value;
        }

        var builder = new StringBuilder(EstimateLength(segments));
        foreach (var segment in segments)
        {
            if (!segment.IsVariable)
            {
                builder.Append(segment.Value);
                continue;
            }

            if (!variables.TryGetValue(segment.Value, out var value))
            {
                throw new MissingPromptVariableException(Name, segment.Value);
            }

            builder.Append(FormatValue(value));
        }

        return builder.ToString();
    }

    /// <summary>Formats a bound value invariantly, so a decimal never renders as "0,2" under a European locale.</summary>
    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static int EstimateLength(MustacheSegment[] segments)
    {
        var length = 0;
        foreach (var segment in segments)
        {
            // Literals contribute their real length; variables get a nominal guess to size the builder.
            length += segment.IsVariable ? 16 : segment.Value.Length;
        }

        return length;
    }

    private static string[] CollectVariables(MustacheSegment[] textSegments, MustacheSegment[][] messageSegments)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in textSegments)
        {
            if (segment.IsVariable && seen.Add(segment.Value))
            {
                found.Add(segment.Value);
            }
        }

        foreach (var segments in messageSegments)
        {
            foreach (var segment in segments)
            {
                if (segment.IsVariable && seen.Add(segment.Value))
                {
                    found.Add(segment.Value);
                }
            }
        }

        return [.. found];
    }

    /// <summary>Builds a prompt from a parsed <c>.prompt.yaml</c> document (the L3 fallback path).</summary>
    internal static LangfusePrompt FromDocument(PromptDocument document, PromptSource source, int version = 0) =>
        new(
            document.Name,
            version,
            document.Kind == PromptKind.Chat ? LangfusePromptType.Chat : LangfusePromptType.Text,
            document.Kind == PromptKind.Text ? document.Text : null,
            [.. document.Messages.Select(m => new LangfuseChatMessage(m.Role, m.Content))],
            [.. document.Labels],
            [.. document.Tags],
            document.Config,
            source);
}
