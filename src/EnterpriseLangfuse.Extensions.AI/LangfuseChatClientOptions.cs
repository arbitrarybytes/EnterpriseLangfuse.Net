using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>Configures <see cref="LangfuseChatClient"/>.</summary>
public sealed class LangfuseChatClientOptions
{
    /// <summary>
    /// Whether prompt and completion content is sent to Langfuse.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>, unlike the OpenTelemetry convention of redacting by
    /// default. Inspecting inputs and outputs is the entire purpose of an LLM observability tool —
    /// a trace without them cannot be debugged or evaluated. Set this to <see langword="false"/> when
    /// prompts carry regulated data and you want timings, tokens and cost without the content.
    /// </remarks>
    public bool CaptureContent { get; set; } = true;

    /// <summary>
    /// Name applied to the generation. Defaults to the model id when unset.
    /// </summary>
    public string? OperationName { get; set; }

    /// <summary>
    /// Resolves the Langfuse user id for a call, enabling per-user analytics.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a fixed value because the user differs per request, while the client
    /// is registered once as a singleton. Typically reads from an ambient
    /// <c>IHttpContextAccessor</c> or <c>AsyncLocal</c>.
    /// </remarks>
    public Func<string?>? UserIdAccessor { get; set; }

    /// <summary>
    /// Resolves the Langfuse session id, which groups a multi-turn conversation.
    /// </summary>
    /// <remarks>
    /// When unset, <see cref="ChatOptions.ConversationId"/> is used, since MEAI already threads that
    /// through a conversation.
    /// </remarks>
    public Func<string?>? SessionIdAccessor { get; set; }

    /// <summary>Tags applied to traces this client creates.</summary>
    public IList<string> Tags { get; } = [];
}
