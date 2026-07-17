using System.Collections.Concurrent;
using EnterpriseLangfuse.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse.Prompts;

/// <summary>
/// L1 — an in-memory cache in front of the pipeline, with request coalescing.
/// </summary>
/// <remarks>
/// Two behaviours matter here beyond plain caching.
/// <para>
/// <b>Coalescing.</b> The cache stores the in-flight <see cref="Task{TResult}"/>, not the resolved
/// prompt. Without this, a cold cache under concurrency lets every simultaneous caller issue its own
/// network fetch — a stampede that hits Langfuse hardest at startup and right after each expiry,
/// exactly when it is least wanted. Storing the task means N concurrent callers share one fetch.
/// </para>
/// <para>
/// <b>Failures are not cached.</b> A faulted task is evicted so the next caller retries rather than
/// inheriting a cached failure for the rest of the TTL.
/// </para>
/// </remarks>
internal sealed class CachingPromptProvider : IPromptProvider
{
    private readonly IPromptProvider _inner;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<LangfuseOptions> _options;

    /// <summary>Guards factory execution per key so only one fetch is started for a cold entry.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<LangfusePrompt>>> _inFlight = new(StringComparer.Ordinal);

    public CachingPromptProvider(
        IPromptProvider inner,
        IMemoryCache cache,
        IOptionsMonitor<LangfuseOptions> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options;
    }

    public Task<LangfusePrompt> GetPromptAsync(
        string name,
        string label = LangfuseDefaults.ProductionLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var key = CacheKey(name, label);

        if (_cache.TryGetValue<Task<LangfusePrompt>>(key, out var cached) && cached is not null)
        {
            // Only a *completed, successful* task is a hit. A still-running task from a concurrent
            // caller is also returned (that is the coalescing), but is not counted as a hit.
            if (cached.IsCompletedSuccessfully)
            {
                LangfuseMetrics.PromptCacheHits.Add(1, new KeyValuePair<string, object?>("prompt.name", name));
                return cached;
            }

            if (!cached.IsCompleted)
            {
                return cached;
            }
        }

        // Counts callers not served from a completed cache entry, not fetches: N coalesced callers
        // record N misses for the single fetch they share. Chosen so hits+misses equals requests,
        // which is what a hit-rate dashboard divides by.
        LangfuseMetrics.PromptCacheMisses.Add(1, new KeyValuePair<string, object?>("prompt.name", name));
        return FetchCoalescedAsync(key, name, label, cancellationToken);
    }

    private Task<LangfusePrompt> FetchCoalescedAsync(
        string key,
        string name,
        string label,
        CancellationToken cancellationToken)
    {
        // Lazy with ExecutionAndPublication guarantees the factory runs at most once per key even if
        // several threads race into GetOrAdd.
        var lazy = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<LangfusePrompt>>(
                () => FetchAndCacheAsync(key, name, label),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var shared = lazy.Value;

        // The slot must live until the fetch settles: removing it any earlier lets a caller that
        // arrives while the fetch is still running miss it and start a duplicate. By the time this
        // runs, a successful fetch has already populated the cache, so later callers hit that
        // instead; a failed fetch leaves nothing behind, so the next caller correctly retries.
        _ = shared.ContinueWith(
            static (completed, state) =>
            {
                var (map, cacheKey) = ((ConcurrentDictionary<string, Lazy<Task<LangfusePrompt>>>, string))state!;
                map.TryRemove(cacheKey, out _);

                // Marks the exception observed. If every coalesced caller cancelled via WaitAsync,
                // nobody awaits the shared task, and a fetch failure would otherwise surface as an
                // UnobservedTaskException on the finalizer thread.
                _ = completed.Exception;
            },
            (_inFlight, key),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Cancellation is observed per caller rather than passed into the shared fetch. The fetch is
        // shared by every coalesced caller, so honouring one caller's token inside it would cancel
        // the others' request too. WaitAsync lets this caller walk away while the fetch continues
        // for everyone still waiting (and still populates the cache).
        return cancellationToken.CanBeCanceled ? shared.WaitAsync(cancellationToken) : shared;
    }

    private async Task<LangfusePrompt> FetchAndCacheAsync(string key, string name, string label)
    {
        var prompt = await _inner.GetPromptAsync(name, label, CancellationToken.None).ConfigureAwait(false);

        if (prompt.Source == PromptSource.EmbeddedFallback)
        {
            LangfuseMetrics.PromptFallbacks.Add(1, new KeyValuePair<string, object?>("prompt.name", name));
        }

        // Cached only on success — reaching here means no exception propagated.
        var ttl = prompt.Source == PromptSource.EmbeddedFallback
            ? _options.CurrentValue.FallbackCacheDuration
            : _options.CurrentValue.PromptCacheDuration;

        if (ttl > TimeSpan.Zero)
        {
            // Discarded: Set returns the cached value, which here is a Task. Without the discard the
            // compiler reads it as a fire-and-forget await candidate (CS4014).
            _ = _cache.Set(
                key,
                Task.FromResult(prompt),
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    // A prompt is small; size 1 lets a SizeLimit-configured cache bound entry count.
                    Size = 1,
                }.SetPriority(CacheItemPriority.Normal));
        }

        return prompt;
    }

    /// <summary>
    /// Cache key. Includes the label because <c>production</c> and <c>staging</c> resolve to different
    /// versions of the same prompt name and must never share an entry.
    /// </summary>
    private static string CacheKey(string name, string label) => $"elf:prompt:{name}:{label}";
}
