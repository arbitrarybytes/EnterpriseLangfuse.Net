using System.Net;
using System.Reflection;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Core.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Prompts;

/// <summary>
/// The spec's headline guarantee: when Langfuse is down, the application keeps serving prompts.
/// </summary>
public sealed class ResiliencePipelineTests
{
    [Fact]
    public async Task Serves_embedded_fallback_when_langfuse_returns_502()
    {
        var handler = StubHttpMessageHandler.AlwaysFails(HttpStatusCode.BadGateway);
        var provider = BuildPipeline(handler);

        var prompt = await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        // The application did not fail, and it knows it is degraded.
        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
        prompt.Name.ShouldBe("RefundAgent");
        prompt.Type.ShouldBe(LangfusePromptType.Chat);
        prompt.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Fallback_prompt_is_usable_not_merely_present()
    {
        // Serving a fallback is worthless if it cannot actually be compiled and sent to a model,
        // so assert the whole path through to rendered output.
        var provider = BuildPipeline(StubHttpMessageHandler.AlwaysFails());

        var compiled = (await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken))
            .Compile(new Dictionary<string, object?> { ["customerName"] = "Ada", ["orderId"] = "A-42" });

        compiled.Messages[1].Content.ShouldBe("Customer Ada is asking about order A-42.");
        compiled.IsFallback.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Falls_back_across_every_transient_status(HttpStatusCode status)
    {
        var provider = BuildPipeline(StubHttpMessageHandler.AlwaysFails(status));

        var prompt = await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
    }

    [Fact]
    public async Task Falls_back_when_the_connection_itself_fails()
    {
        // Not every outage is a status code — DNS failures and resets never reach an HTTP response.
        var provider = BuildPipeline(StubHttpMessageHandler.AlwaysThrows(new HttpRequestException("no such host")));

        var prompt = await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
    }

    [Fact]
    public async Task Falls_back_when_langfuse_returns_an_unparseable_body()
    {
        // A 200 carrying garbage is a real failure mode (proxies, captive portals, HTML error pages).
        var provider = BuildPipeline(StubHttpMessageHandler.AlwaysReturnsJson("<html>gateway error</html>"));

        var prompt = await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
    }

    [Fact]
    public async Task Falls_back_when_the_prompt_is_missing_from_langfuse()
    {
        // A prompt added to code but not yet synced must still work locally.
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var prompt = await BuildPipeline(handler).GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
    }

    [Fact]
    public async Task Throws_when_langfuse_is_down_and_no_fallback_was_embedded()
    {
        // The genuinely unrecoverable case must be loud, and must explain the remedy.
        var provider = BuildPipeline(StubHttpMessageHandler.AlwaysFails());

        var error = await Should.ThrowAsync<PromptNotFoundException>(
            () => provider.GetPromptAsync("NeverEmbedded", cancellationToken: TestContext.Current.CancellationToken));

        error.PromptName.ShouldBe("NeverEmbedded");
        error.InnerException.ShouldNotBeNull();
        error.Message.ShouldContain("EmbeddedResource");
    }

    [Fact]
    public async Task Prefers_langfuse_over_the_embedded_copy_when_the_network_is_healthy()
    {
        // The fallback must never shadow the live prompt — that would freeze production at build time.
        var handler = StubHttpMessageHandler.AlwaysReturnsJson(
            """
            {"name":"RefundAgent","version":9,"type":"chat","labels":["production"],
             "prompt":[{"role":"system","content":"Live from Langfuse {{customerName}}"}]}
            """);

        var prompt = await BuildPipeline(handler).GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.Network);
        prompt.Version.ShouldBe(9);
        prompt.Messages[0].Content.ShouldBe("Live from Langfuse {{customerName}}");
    }

    [Fact]
    public async Task Fallback_can_be_disabled_for_callers_who_prefer_to_fail_loudly()
    {
        var provider = BuildPipeline(
            StubHttpMessageHandler.AlwaysFails(),
            options => options.EnableOfflineFallback = false);

        await Should.ThrowAsync<HttpRequestException>(() => provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_answered_with_a_stale_fallback()
    {
        // A cancelled request should surface as cancellation, not silently degrade to a fallback.
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(async (_, _) =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = BuildPipeline(handler);

        await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetPromptAsync("RefundAgent", LangfuseDefaults.ProductionLabel, cts.Token));
    }

    /// <summary>
    /// Builds the real three-tier pipeline over a stubbed transport — the same composition
    /// <c>AddEnterpriseLangfuse</c> produces, so these tests exercise production wiring.
    /// </summary>
    private static IPromptProvider BuildPipeline(
        HttpMessageHandler handler,
        Action<LangfuseOptions>? configure = null)
    {
        var options = new LangfuseOptions
        {
            PublicKey = "pk-lf-test",
            SecretKey = "sk-lf-test",
            BaseUrl = new Uri("https://langfuse.test"),
        };
        configure?.Invoke(options);

        var monitor = new StaticOptionsMonitor<LangfuseOptions>(options);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://langfuse.test/") };
        var api = new LangfuseApi(httpClient, NullLogger<LangfuseApi>.Instance);

        var store = new EmbeddedPromptStore(
            [Assembly.GetExecutingAssembly()],
            NullLogger<EmbeddedPromptStore>.Instance);

        var network = new LangfusePromptProvider(api);
        var withFallback = new FallbackPromptProvider(network, store, monitor, NullLogger<FallbackPromptProvider>.Instance);

        return new CachingPromptProvider(withFallback, new MemoryCache(new MemoryCacheOptions()), monitor);
    }
}

/// <summary>A minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
