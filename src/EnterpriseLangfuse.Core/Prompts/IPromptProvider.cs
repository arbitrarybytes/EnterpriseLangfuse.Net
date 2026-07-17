namespace EnterpriseLangfuse.Prompts;

/// <summary>
/// Resolves prompts by name, guaranteeing a result whenever a fallback exists.
/// </summary>
/// <remarks>
/// The shipped implementation is a three-tier pipeline — L1 in-memory cache, L2 the Langfuse API,
/// L3 an embedded <c>.prompt.yaml</c> — so a Langfuse outage degrades to serving the version compiled
/// into the assembly rather than throwing. This interface is also the extension point that
/// EnterpriseLangfuse.Generators hangs its strongly typed, per-prompt methods off.
/// </remarks>
public interface IPromptProvider
{
    /// <summary>
    /// Resolves a prompt, preferring the freshest source available.
    /// </summary>
    /// <param name="name">The prompt's name in Langfuse.</param>
    /// <param name="label">The label to resolve, e.g. <c>production</c>.</param>
    /// <param name="cancellationToken">Cancels the network fetch.</param>
    /// <exception cref="PromptNotFoundException">
    /// Neither Langfuse nor an embedded fallback could supply the prompt.
    /// </exception>
    Task<LangfusePrompt> GetPromptAsync(
        string name,
        string label = LangfuseDefaults.ProductionLabel,
        CancellationToken cancellationToken = default);
}

/// <summary>Shared defaults, kept in one place so the generator and runtime cannot drift apart.</summary>
public static class LangfuseDefaults
{
    /// <summary>The label resolved when a caller does not specify one.</summary>
    public const string ProductionLabel = "production";
}
