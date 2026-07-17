using EnterpriseLangfuse.Api;

namespace EnterpriseLangfuse.Prompts;

/// <summary>
/// L2 — fetches prompts from the Langfuse API.
/// </summary>
/// <remarks>
/// Deliberately has no caching or fallback behaviour of its own; it is the innermost tier of the
/// pipeline and lets transport failures propagate so the outer tiers can decide what to do.
/// </remarks>
internal sealed class LangfusePromptProvider : IPromptProvider
{
    private readonly ILangfuseApi _api;

    public LangfusePromptProvider(ILangfuseApi api) => _api = api;

    public async Task<LangfusePrompt> GetPromptAsync(
        string name,
        string label = LangfuseDefaults.ProductionLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var prompt = await _api.GetPromptAsync(name, label, cancellationToken).ConfigureAwait(false);

        // A 404 is authoritative, not transient: Langfuse answered and said this prompt does not
        // exist. The fallback tier still gets a chance to serve an embedded copy.
        return prompt ?? throw new PromptNotFoundException(name, label);
    }
}
