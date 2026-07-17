// Shared source, linked into EnterpriseLangfuse.Core (net10.0), .Generators (netstandard2.0)
// and .MSBuild. It must therefore compile under netstandard2.0: no records, no `required`,
// no `init` accessors (all need runtime support or polyfills that Roslyn analyzers ship without).

using System.Collections.Generic;

namespace EnterpriseLangfuse.Shared;

/// <summary>Prompt kind, mirroring Langfuse's text/chat discriminator.</summary>
internal enum PromptKind
{
    Text,
    Chat,
}

/// <summary>A single chat message in a chat-typed prompt.</summary>
internal sealed class PromptMessageDocument
{
    public PromptMessageDocument(string role, string content)
    {
        Role = role;
        Content = content;
    }

    public string Role { get; }

    public string Content { get; }
}

/// <summary>
/// The parsed, provider-agnostic representation of a <c>.prompt.yaml</c> file. This is the single
/// schema shared by the runtime fallback loader, the source generator and the MSBuild sync task,
/// so all three agree on what a prompt file means.
/// </summary>
internal sealed class PromptDocument
{
    public string Name { get; set; } = string.Empty;

    public PromptKind Kind { get; set; } = PromptKind.Text;

    /// <summary>Body for <see cref="PromptKind.Text"/> prompts; empty for chat prompts.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Body for <see cref="PromptKind.Chat"/> prompts; empty for text prompts.</summary>
    public List<PromptMessageDocument> Messages { get; } = new List<PromptMessageDocument>();

    public List<string> Labels { get; } = new List<string>();

    public List<string> Tags { get; } = new List<string>();

    /// <summary>Free-form model configuration (model name, temperature, ...) passed through verbatim.</summary>
    public Dictionary<string, object?> Config { get; } = new Dictionary<string, object?>();

    public string? CommitMessage { get; set; }

    /// <summary>
    /// Every distinct <c>{{mustache}}</c> variable referenced by this prompt's body, in first-seen
    /// order. Drives both the generated method signature and runtime compilation validation.
    /// </summary>
    public IReadOnlyList<string> Variables
    {
        get
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (Kind == PromptKind.Text)
            {
                MustacheParser.CollectVariables(Text, found, seen);
            }
            else
            {
                foreach (var message in Messages)
                {
                    MustacheParser.CollectVariables(message.Content, found, seen);
                }
            }

            return found;
        }
    }
}
