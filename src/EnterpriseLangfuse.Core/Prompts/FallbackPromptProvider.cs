using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse.Prompts;

/// <summary>
/// L3 — serves an embedded <c>.prompt.yaml</c> when Langfuse cannot answer.
/// </summary>
/// <remarks>
/// This is the tier that turns a Langfuse outage from an application outage into a logged warning.
/// <para>
/// What it catches is deliberately narrow. Transport failures (<see cref="HttpRequestException"/>,
/// timeouts) and <see cref="PromptNotFoundException"/> fall back. Everything else — including
/// <see cref="OperationCanceledException"/> from the caller's own token — propagates: a cancelled
/// request should not be answered with a stale prompt, and swallowing unknown exceptions here would
/// mask real bugs behind silently degraded serving.
/// </para>
/// </remarks>
internal sealed class FallbackPromptProvider : IPromptProvider
{
    private readonly IPromptProvider _inner;
    private readonly IEmbeddedPromptStore _store;
    private readonly ILogger<FallbackPromptProvider> _logger;
    private readonly IOptionsMonitor<LangfuseOptions> _options;

    public FallbackPromptProvider(
        IPromptProvider inner,
        IEmbeddedPromptStore store,
        IOptionsMonitor<LangfuseOptions> options,
        ILogger<FallbackPromptProvider> logger)
    {
        _inner = inner;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task<LangfusePrompt> GetPromptAsync(
        string name,
        string label = LangfuseDefaults.ProductionLabel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.GetPromptAsync(name, label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            if (!_options.CurrentValue.EnableOfflineFallback)
            {
                throw;
            }

            var fallback = _store.TryGet(name);
            if (fallback is null)
            {
                // Nothing to serve. Preserve the original failure as the inner exception so the
                // operator can see *why* Langfuse could not answer, not just that it did not.
                throw ex is PromptNotFoundException notFound
                    ? notFound
                    : new PromptNotFoundException(name, label, ex);
            }

            _logger.ServingEmbeddedFallback(name, ex);
            return fallback;
        }
    }

    /// <summary>
    /// True for failures where an embedded copy is a legitimate answer.
    /// </summary>
    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        // The caller gave up; do not hand them a fallback they never waited for.
        OperationCanceledException when cancellationToken.IsCancellationRequested => false,

        // A timeout surfaces as TaskCanceledException with an unsignalled token.
        TaskCanceledException or TimeoutException => true,

        // 401/403 are authoritative misconfiguration, not an outage: falling back would mask wrong
        // credentials behind silently degraded serving until someone notices staleness. Fail loudly.
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => false,

        HttpRequestException => true,

        // Thrown by the resilience pipeline once its circuit opens (BrokenCircuitException) or its
        // total timeout rejects the attempt (TimeoutRejectedException) — i.e. during a *sustained*
        // outage, the exact situation the fallback tier exists for.
        Polly.ExecutionRejectedException => true,

        // Langfuse answered "no such prompt". An embedded copy may still exist — e.g. a prompt that
        // has been added to the code but not yet synced to Langfuse.
        PromptNotFoundException => true,

        // Langfuse answered with something unreadable. Serving the known-good embedded copy beats
        // failing the request on a payload we cannot parse.
        LangfuseApiException => true,

        _ => false,
    };
}
