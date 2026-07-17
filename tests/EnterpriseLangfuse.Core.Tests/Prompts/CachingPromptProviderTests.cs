using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Prompts;

public sealed class CachingPromptProviderTests
{
    [Fact]
    public async Task Serves_a_second_request_from_cache_without_hitting_the_network()
    {
        var inner = new CountingProvider();
        var provider = Build(inner, out _);

        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);
        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Refetches_once_the_ttl_expires()
    {
        var inner = new CountingProvider();
        var provider = Build(inner, out var cache, ttl: TimeSpan.FromMilliseconds(30));

        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        // MemoryCache reads the system clock directly, so this genuinely has to elapse. Kept tiny.
        await Task.Delay(80, TestContext.Current.CancellationToken);

        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(2);
        cache.Dispose();
    }

    [Fact]
    public async Task Caches_per_label_so_staging_never_serves_production()
    {
        // production and staging resolve to different versions of the same name; sharing an entry
        // would ship the wrong prompt to production.
        var inner = new CountingProvider();
        var provider = Build(inner, out _);

        await provider.GetPromptAsync("P", "production", TestContext.Current.CancellationToken);
        await provider.GetPromptAsync("P", "staging", TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Coalesces_concurrent_cold_requests_into_one_fetch()
    {
        // The stampede guard: without it, N concurrent callers each fetch on a cold cache, hitting
        // Langfuse hardest at startup and at every expiry.
        var gate = new TaskCompletionSource();
        var inner = new CountingProvider(gate.Task);
        var provider = Build(inner, out _);

        var requests = Enumerable.Range(0, 50)
            .Select(_ => provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(requests);

        inner.Calls.ShouldBe(1);
        requests.ShouldAllBe(r => r.Result.Name == "P");
    }

    [Fact]
    public async Task Does_not_cache_a_failure()
    {
        // A cached failure would keep failing for the whole TTL even after Langfuse recovered.
        var inner = new CountingProvider { Failure = new HttpRequestException("down") };
        var provider = Build(inner, out _);

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken));

        inner.Failure = null;
        var recovered = await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        recovered.Name.ShouldBe("P");
        inner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task One_caller_cancelling_does_not_cancel_the_others_sharing_the_fetch()
    {
        // Coalescing means callers share one fetch; honouring one caller's token inside it would
        // cancel everyone else's request too.
        var gate = new TaskCompletionSource();
        var inner = new CountingProvider(gate.Task);
        var provider = Build(inner, out _);

        using var cts = new CancellationTokenSource();
        var abandoned = provider.GetPromptAsync("P", cancellationToken: cts.Token);
        var patient = provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => abandoned);

        gate.SetResult();

        (await patient).Name.ShouldBe("P");
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Caches_a_fallback_only_briefly_so_recovery_is_noticed()
    {
        // Caching a fallback for the full TTL would keep serving the stale embedded copy long after
        // Langfuse came back.
        var inner = new CountingProvider { Source = PromptSource.EmbeddedFallback };
        var provider = Build(
            inner,
            out var cache,
            ttl: TimeSpan.FromMinutes(5),
            fallbackTtl: TimeSpan.FromMilliseconds(30));

        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await provider.GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(2);
        cache.Dispose();
    }

    [Fact]
    public async Task Rejects_a_blank_prompt_name()
    {
        var provider = Build(new CountingProvider(), out _);

        await Should.ThrowAsync<ArgumentException>(
            () => provider.GetPromptAsync("  ", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static IPromptProvider Build(
        IPromptProvider inner,
        out MemoryCache cache,
        TimeSpan? ttl = null,
        TimeSpan? fallbackTtl = null)
    {
        cache = new MemoryCache(new MemoryCacheOptions());

        var options = new StaticOptionsMonitor<LangfuseOptions>(new LangfuseOptions
        {
            PublicKey = "pk",
            SecretKey = "sk",
            PromptCacheDuration = ttl ?? TimeSpan.FromMinutes(1),
            FallbackCacheDuration = fallbackTtl ?? TimeSpan.FromSeconds(10),
        });

        return new CachingPromptProvider(inner, cache, options);
    }

    /// <summary>An inner tier that counts fetches and can be gated or made to fail.</summary>
    private sealed class CountingProvider(Task? gate = null) : IPromptProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Exception? Failure { get; set; }

        public PromptSource Source { get; set; } = PromptSource.Network;

        public async Task<LangfusePrompt> GetPromptAsync(
            string name,
            string label = LangfuseDefaults.ProductionLabel,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            if (gate is not null)
            {
                await gate;
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new LangfusePrompt(
                name,
                1,
                LangfusePromptType.Text,
                "body",
                [],
                [],
                [],
                new Dictionary<string, object?>(),
                Source);
        }
    }
}
