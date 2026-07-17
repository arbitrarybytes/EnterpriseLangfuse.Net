using System.Net;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse.Benchmarks;

/// <summary>
/// Substantiates the README's claims about the resilience pipeline.
/// </summary>
/// <remarks>
/// The transport is a stub that returns instantly, so a "network" call here costs far less than a
/// real one. That makes the comparison <em>conservative</em>: against real Langfuse latency the
/// cache-hit advantage is larger than these numbers, never smaller. What is measured is the
/// framework's own overhead, which is the only part this library controls.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class PromptPipelineBenchmarks
{
    private IPromptProvider _pipeline = null!;
    private IPromptProvider _coldPipeline = null!;
    private IPromptProvider _outagePipeline = null!;
    private LangfusePrompt _prompt = null!;
    private Dictionary<string, object?> _variables = null!;

    private const string PromptJson =
        """
        {"name":"RefundAgent","version":3,"type":"chat","labels":["production"],
         "config":{"model":"claude-opus-4-8","temperature":0.2},
         "prompt":[{"role":"system","content":"You are a refund agent. Be concise."},
                   {"role":"user","content":"Customer {{customerName}} is asking about order {{orderId}}."}]}
        """;

    [GlobalSetup]
    public void Setup()
    {
        _variables = new Dictionary<string, object?> { ["customerName"] = "Ada Lovelace", ["orderId"] = "A-4815162342" };

        _pipeline = BuildPipeline(Healthy());
        // One tick, not zero: a zero TTL skips the cache Set entirely, under-measuring the very path
        // these benchmarks claim to substantiate. One tick pays the Set and is already expired by the
        // next call, so every iteration is still a genuine miss.
        _coldPipeline = BuildPipeline(Healthy(), cacheTtl: TimeSpan.FromTicks(1));
        _outagePipeline = BuildPipeline(Failing(), cacheTtl: TimeSpan.FromTicks(1), fallbackTtl: TimeSpan.FromTicks(1));

        // Warm the L1 cache so CacheHit measures a hit rather than the first miss.
        _pipeline.GetPromptAsync("RefundAgent").GetAwaiter().GetResult();
        _prompt = _outagePipeline.GetPromptAsync("RefundAgent").GetAwaiter().GetResult();
    }

    /// <summary>The steady-state cost of resolving a prompt: what a production request actually pays.</summary>
    [Benchmark(Baseline = true, Description = "Resolve prompt (L1 cache hit)")]
    public async Task<LangfusePrompt> CacheHit() => await _pipeline.GetPromptAsync("RefundAgent");

    /// <summary>
    /// The cost when the cache is cold and the pipeline must go to the API and re-map the payload.
    /// Excludes real network latency, so the true gap is far wider than shown.
    /// </summary>
    [Benchmark(Description = "Resolve prompt (cache miss, stub transport)")]
    public async Task<LangfusePrompt> CacheMiss() => await _coldPipeline.GetPromptAsync("RefundAgent");

    /// <summary>
    /// Resolving during a total Langfuse outage. This is the number behind "zero-downtime": the
    /// application keeps serving, and it does so without paying a penalty.
    /// </summary>
    [Benchmark(Description = "Resolve prompt (Langfuse down, embedded fallback)")]
    public async Task<LangfusePrompt> OutageFallback() => await _outagePipeline.GetPromptAsync("RefundAgent");

    /// <summary>
    /// Rendering a prompt's variables. Templates are parsed once at fetch, so this is a pure render —
    /// the claim the README makes about compile cost.
    /// </summary>
    [Benchmark(Description = "Compile prompt (2 variables, 2 messages)")]
    public CompiledPrompt Compile() => _prompt.Compile(_variables);

    private static StubHandler Healthy() => new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(PromptJson, System.Text.Encoding.UTF8, "application/json"),
    });

    private static StubHandler Failing() => new((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

    private static IPromptProvider BuildPipeline(
        HttpMessageHandler handler,
        TimeSpan? cacheTtl = null,
        TimeSpan? fallbackTtl = null)
    {
        var options = new StaticOptions(new LangfuseOptions
        {
            PublicKey = "pk",
            SecretKey = "sk",
            PromptCacheDuration = cacheTtl ?? TimeSpan.FromMinutes(1),
            FallbackCacheDuration = fallbackTtl ?? TimeSpan.FromSeconds(10),
        });

        var api = new LangfuseApi(
            new HttpClient(handler) { BaseAddress = new Uri("https://langfuse.test/") },
            NullLogger<LangfuseApi>.Instance);

        var store = new EmbeddedPromptStore([Assembly.GetExecutingAssembly()], NullLogger<EmbeddedPromptStore>.Instance);
        var network = new LangfusePromptProvider(api);
        var fallback = new FallbackPromptProvider(network, store, options, NullLogger<FallbackPromptProvider>.Instance);

        return new CachingPromptProvider(fallback, new MemoryCache(new MemoryCacheOptions()), options);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }

    internal sealed class StaticOptions(LangfuseOptions value) : IOptionsMonitor<LangfuseOptions>
    {
        public LangfuseOptions CurrentValue { get; } = value;

        public LangfuseOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<LangfuseOptions, string?> listener) => null;
    }
}
