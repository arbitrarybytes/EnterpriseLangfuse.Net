using System.Net;
using EnterpriseLangfuse.Core.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Prompts;

/// <summary>Regression coverage for defects found in the full-codebase review.</summary>
public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task Falls_back_when_the_resilience_circuit_is_open()
    {
        // A sustained outage eventually surfaces as BrokenCircuitException from the resilience
        // pipeline, not HttpRequestException. The fallback tier must recognise it — this is the
        // exact situation it exists for.
        var inner = new ThrowingProvider(new BrokenCircuitException("circuit open"));
        var provider = BuildFallback(inner);

        var prompt = await provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.EmbeddedFallback);
    }

    [Fact]
    public async Task Does_not_mask_bad_credentials_behind_the_fallback()
    {
        // 401 is authoritative misconfiguration. Serving the embedded copy would hide wrong keys
        // behind silently degraded serving until someone notices the prompts are stale.
        var inner = new ThrowingProvider(new HttpRequestException("unauthorized", inner: null, HttpStatusCode.Unauthorized));
        var provider = BuildFallback(inner);

        await Should.ThrowAsync<HttpRequestException>(
            () => provider.GetPromptAsync("RefundAgent", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Request_timeout_maps_to_the_resilience_attempt_not_the_whole_chain()
    {
        // HttpClient.Timeout wraps every retry, so pointing RequestTimeout at it means one slow
        // attempt exhausts the budget and no retry ever runs. The mapping goes to the standard
        // resilience options instead — and this asserts the "-standard" options name still exists,
        // so an upstream rename fails this test rather than silently reverting us to defaults.
        var services = new ServiceCollection();
        services.AddEnterpriseLangfuse(o =>
        {
            o.PublicKey = "pk";
            o.SecretKey = "sk";
            o.RequestTimeout = TimeSpan.FromSeconds(7);
        });

        using var provider = services.BuildServiceProvider();

        var resilience = provider
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{ServiceCollectionExtensions.HttpClientName}-standard");

        resilience.AttemptTimeout.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
        resilience.TotalRequestTimeout.Timeout.ShouldBe(TimeSpan.FromSeconds(28));
        resilience.CircuitBreaker.SamplingDuration.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(14));
    }

    [Fact]
    public void Rejects_a_yaml_alias_bomb_instead_of_expanding_it()
    {
        // Anchored doubling: each level references the previous twice. Materialised naively this is
        // 2^24 nodes from a 30-line file; the parser must refuse, not hang the IDE.
        var yaml = "name: Bomb\nprompt: hi\nconfig:\n  a: &a [x, x, x, x, x, x, x, x]\n";
        for (var i = 1; i < 24; i++)
        {
            yaml += $"  {(char)('a' + i)}: &{(char)('a' + i)} [*{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}]\n";
        }

        var error = Should.Throw<PromptYamlException>(() => PromptYamlParser.Parse(yaml));

        error.Message.ShouldContain("alias bomb");
    }

    private static IPromptProvider BuildFallback(IPromptProvider inner)
    {
        var store = new EmbeddedPromptStore(
            [typeof(ReviewRegressionTests).Assembly],
            NullLogger<EmbeddedPromptStore>.Instance);

        var options = new StaticOptionsMonitor<LangfuseOptions>(new LangfuseOptions { PublicKey = "pk", SecretKey = "sk" });

        return new FallbackPromptProvider(inner, store, options, NullLogger<FallbackPromptProvider>.Instance);
    }

    private sealed class ThrowingProvider(Exception failure) : IPromptProvider
    {
        public Task<LangfusePrompt> GetPromptAsync(
            string name,
            string label = LangfuseDefaults.ProductionLabel,
            CancellationToken cancellationToken = default) => Task.FromException<LangfusePrompt>(failure);
    }
}
