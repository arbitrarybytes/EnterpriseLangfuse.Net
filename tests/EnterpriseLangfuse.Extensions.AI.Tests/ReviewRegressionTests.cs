using System.Diagnostics;
using EnterpriseLangfuse.Extensions.AI.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.AI;
using Shouldly;

namespace EnterpriseLangfuse.Extensions.AI.Tests;

/// <summary>Regression coverage for defects found in the full-codebase review.</summary>
public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task The_prompt_never_travels_to_the_inner_client()
    {
        // Providers may serialise AdditionalProperties into their request body; a live
        // CompiledPrompt there would leak prompt metadata or break their serialiser.
        var telemetry = new RecordingTelemetry();
        var inner = new OptionsCapturingClient();
        using var client = new LangfuseChatClient(inner, telemetry);

        var prompt = BuildPrompt();
        var options = new ChatOptions { ModelId = "m" }.WithLangfusePrompt(prompt);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken);

        // The generation is still linked (read before the strip)...
        telemetry.Generations.ShouldHaveSingleItem().PromptName.ShouldBe("P");

        // ...but the inner client saw clean options, and the caller's instance is untouched.
        inner.SeenOptions.ShouldNotBeNull();
        (inner.SeenOptions!.AdditionalProperties?.ContainsKey(LangfusePromptContext.PropertyKey) ?? false).ShouldBeFalse();
        options.AdditionalProperties!.ContainsKey(LangfusePromptContext.PropertyKey).ShouldBeTrue();
        inner.SeenOptions.ModelId.ShouldBe("m");
    }

    [Fact]
    public async Task Session_falls_back_to_the_meai_conversation_id()
    {
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ConversationId = "thread-42" },
            TestContext.Current.CancellationToken);

        telemetry.Traces.ShouldHaveSingleItem().SessionId.ShouldBe("thread-42");
    }

    [Fact]
    public async Task A_foreign_parent_span_does_not_become_a_parent_observation()
    {
        // An ambient ASP.NET Core span has a valid span id that references a Langfuse observation
        // that will never exist; using it orphans the generation in the UI.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        using var foreignSource = new ActivitySource("aspnetcore-fake");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using (foreignSource.StartActivity("http-request"))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        }

        telemetry.Generations.ShouldHaveSingleItem().ParentObservationId.ShouldBeNull();
    }

    [Fact]
    public async Task Nested_langfuse_spans_do_link_parent_observations()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var outer = new LangfuseChatClient(
            new NestingClient(new LangfuseChatClient(new FakeChatClient(response), telemetry)),
            telemetry);

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Diagnostics.LangfuseMetrics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await outer.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.Count.ShouldBe(2);

        // The inner call ran under the outer's Langfuse span, so it must reference it.
        var outerGeneration = telemetry.Generations[^1];
        var innerGeneration = telemetry.Generations[0];
        innerGeneration.ParentObservationId.ShouldBe(outerGeneration.Id);
    }

    [Fact]
    public async Task Streaming_without_content_capture_still_reports_usage()
    {
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(
            new FakeChatClient(() =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, "chunk"),
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 9 })]),
            ]),
            telemetry,
            new LangfuseChatClientOptions { CaptureContent = false });

        using (client)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Output.ShouldBeNull();
        generation.Usage!["input"].ShouldBe(5);
        generation.Usage["output"].ShouldBe(9);
    }

    private static CompiledPrompt BuildPrompt() => new LangfusePrompt(
        "P",
        1,
        LangfusePromptType.Text,
        "hi",
        [],
        [],
        [],
        new Dictionary<string, object?>(),
        PromptSource.Network).Compile();

    /// <summary>Captures the ChatOptions the inner client actually receives.</summary>
    private sealed class OptionsCapturingClient : IChatClient
    {
        public ChatOptions? SeenOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SeenOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>An inner "provider" that itself makes a traced LLM call, creating a nested span.</summary>
    private sealed class NestingClient(IChatClient nested) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            await nested.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
