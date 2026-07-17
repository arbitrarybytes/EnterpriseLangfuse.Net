using System.Net;
using System.Reflection;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Core.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Langfuse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.DependencyInjection;

/// <summary>
/// Asserts the wiring a consumer actually gets from <c>AddEnterpriseLangfuse</c>.
/// </summary>
public sealed class AddEnterpriseLangfuseTests
{
    [Fact]
    public void Registers_everything_a_consumer_needs()
    {
        using var provider = Build();

        provider.GetService<IPromptProvider>().ShouldNotBeNull();
        provider.GetService<ILangfuseTelemetry>().ShouldNotBeNull();
        provider.GetService<ILangfuseClient>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().OfType<LangfuseTelemetryDispatcher>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Composes_the_pipeline_as_cache_over_fallback_over_network()
    {
        // The tier order is the design. A mis-ordered pipeline would cache failures or let the
        // embedded copy shadow the live prompt, so assert the actual composition rather than trusting it.
        using var provider = Build();

        var cache = provider.GetRequiredService<IPromptProvider>().ShouldBeOfType<CachingPromptProvider>();
        var fallback = Inner(cache).ShouldBeOfType<FallbackPromptProvider>();
        Inner(fallback).ShouldBeOfType<LangfusePromptProvider>();
    }

    [Fact]
    public void Prompt_provider_and_telemetry_are_singletons()
    {
        // A transient cache would defeat caching entirely; a transient channel would silently split
        // the queue so the dispatcher drained only one of them.
        using var provider = Build();

        provider.GetRequiredService<IPromptProvider>().ShouldBeSameAs(provider.GetRequiredService<IPromptProvider>());
        provider.GetRequiredService<ILangfuseTelemetry>().ShouldBeSameAs(provider.GetRequiredService<ILangfuseTelemetry>());
    }

    [Fact]
    public async Task Sends_basic_auth_and_a_well_formed_url()
    {
        var handler = StubHttpMessageHandler.AlwaysReturnsJson(
            """{"name":"P","version":1,"type":"text","prompt":"hi"}""");

        using var provider = Build(
            o =>
            {
                o.PublicKey = "pk-lf-123";
                o.SecretKey = "sk-lf-456";
            },
            handler);

        await provider.GetRequiredService<IPromptProvider>()
            .GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Authorization.ShouldBe($"Basic {Convert.ToBase64String("pk-lf-123:sk-lf-456"u8.ToArray())}");
        request.Uri!.AbsolutePath.ShouldBe("/api/public/v2/prompts/P");
        request.Uri.Query.ShouldContain("label=production");
    }

    [Fact]
    public async Task Preserves_the_path_of_a_self_hosted_base_url()
    {
        // Without a trailing slash, Uri resolution silently drops the last path segment — a
        // self-hosted deployment behind a path prefix would 404 with no obvious cause.
        var handler = StubHttpMessageHandler.AlwaysReturnsJson("""{"name":"P","version":1,"type":"text","prompt":"hi"}""");

        using var provider = Build(o => o.BaseUrl = new Uri("https://internal.example.com/langfuse"), handler);

        await provider.GetRequiredService<IPromptProvider>()
            .GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().Uri!.AbsoluteUri
            .ShouldBe("https://internal.example.com/langfuse/api/public/v2/prompts/P?label=production");
    }

    [Fact]
    public async Task Escapes_a_prompt_name_containing_a_slash()
    {
        // Prompt names routinely look like `support/refund agent`; unescaped they would change the route.
        var handler = StubHttpMessageHandler.AlwaysReturnsJson("""{"name":"a/b","version":1,"type":"text","prompt":"hi"}""");

        using var provider = Build(handler: handler);

        await provider.GetRequiredService<IPromptProvider>()
            .GetPromptAsync("support/refund agent", cancellationToken: TestContext.Current.CancellationToken);

        handler.Requests.ShouldHaveSingleItem().Uri!.AbsoluteUri.ShouldContain("support%2Frefund%20agent");
    }

    [Fact]
    public async Task Retries_a_transient_failure_before_giving_up()
    {
        // The resilience handler is what makes a blip a non-event; the L3 fallback should only
        // engage once retrying has genuinely failed.
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(
            call < 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"name":"P","version":4,"type":"text","prompt":"recovered"}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                }));

        using var provider = Build(handler: handler);

        var prompt = await provider.GetRequiredService<IPromptProvider>()
            .GetPromptAsync("P", cancellationToken: TestContext.Current.CancellationToken);

        prompt.Source.ShouldBe(PromptSource.Network);
        prompt.Version.ShouldBe(4);
        handler.CallCount.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Rejects_missing_credentials_at_startup_with_an_actionable_message()
    {
        // Failing the deployment beats failing the first request that needs a prompt.
        var services = new ServiceCollection();
        services.AddEnterpriseLangfuse(o => o.PublicKey = "pk-only");

        using var provider = services.BuildServiceProvider();

        var error = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<LangfuseOptions>>().Value);

        error.Message.ShouldContain("SecretKey is required");
        error.Message.ShouldContain("Settings > API Keys");
    }

    [Fact]
    public void Rejects_a_batch_larger_than_the_queue()
    {
        // Silently unfillable batches would stall every flush until the interval elapsed.
        var services = new ServiceCollection();
        services.AddEnterpriseLangfuse(o =>
        {
            o.PublicKey = "pk";
            o.SecretKey = "sk";
            o.TelemetryQueueCapacity = 10;
            o.TelemetryBatchSize = 100;
        });

        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LangfuseOptions>>().Value)
            .Message.ShouldContain("can never be filled");
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddEnterpriseLangfuse(_ => { }));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddEnterpriseLangfuse(null!));
    }

    [Fact]
    public async Task Telemetry_flows_from_track_to_the_ingestion_endpoint()
    {
        // End-to-end through the real DI graph: Track -> channel -> hosted dispatcher -> HTTP.
        var handler = StubHttpMessageHandler.AlwaysReturnsJson("""{"successes":[],"errors":[]}""");
        using var provider = Build(handler: handler);

        var dispatcher = provider.GetServices<IHostedService>().OfType<LangfuseTelemetryDispatcher>().Single();
        await dispatcher.StartAsync(TestContext.Current.CancellationToken);

        var telemetry = provider.GetRequiredService<ILangfuseTelemetry>();
        telemetry.Track(new LangfuseGeneration { Name = "chat", Model = "claude-opus-4-8" });

        await telemetry.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await dispatcher.StopAsync(TestContext.Current.CancellationToken);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri!.AbsolutePath.ShouldBe("/api/public/ingestion");
        request.Body.ShouldNotBeNull();
        request.Body!.ShouldContain("generation-create");
        request.Body!.ShouldContain("claude-opus-4-8");
    }

    /// <summary>Reads a decorator's wrapped provider, to assert the pipeline's real shape.</summary>
    private static IPromptProvider Inner(IPromptProvider provider) =>
        (IPromptProvider)provider.GetType()
            .GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider)!;

    private static ServiceProvider Build(
        Action<LangfuseOptions>? configure = null,
        HttpMessageHandler? handler = null)
    {
        var services = new ServiceCollection();

        services.AddEnterpriseLangfuse(o =>
        {
            o.PublicKey = "pk-lf-test";
            o.SecretKey = "sk-lf-test";
            o.BaseUrl = new Uri("https://langfuse.test");
            o.TelemetryFlushInterval = TimeSpan.FromMilliseconds(20);
            o.FallbackAssemblies.Add(Assembly.GetExecutingAssembly());
            configure?.Invoke(o);
        });

        if (handler is not null)
        {
            // Replace the transport while leaving the resilience pipeline in place, so these tests
            // exercise the real handler chain rather than a bypass of it.
            var primary = handler;
            services.AddHttpClient(ServiceCollectionExtensions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => primary);
        }

        return services.BuildServiceProvider();
    }
}
