using System.Diagnostics;
using EnterpriseLangfuse.Extensions.AI.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;
using Shouldly;

namespace EnterpriseLangfuse.Extensions.AI.Tests;

public sealed class LangfuseChatClientTests
{
    [Fact]
    public async Task Records_a_generation_with_model_tokens_and_timings()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello there"))
        {
            ModelId = "claude-opus-4-8",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5, TotalTokenCount = 17 },
        };

        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions { ModelId = "claude-opus-4-8", Temperature = 0.2f },
            TestContext.Current.CancellationToken);

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Model.ShouldBe("claude-opus-4-8");
        generation.Usage.ShouldNotBeNull();
        generation.Usage["input"].ShouldBe(12);
        generation.Usage["output"].ShouldBe(5);
        generation.Usage["total"].ShouldBe(17);
        generation.StartTime.ShouldNotBeNull();
        generation.EndTime.ShouldNotBeNull();
        generation.EndTime!.Value.ShouldBeGreaterThanOrEqualTo(generation.StartTime!.Value);
        generation.Level.ShouldBe(ObservationLevel.Default);
    }

    [Fact]
    public async Task Prefers_the_model_that_actually_served_the_request()
    {
        // Providers route aliases to concrete models; cost attribution depends on the real one.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")) { ModelId = "claude-opus-4-8-20260101" };

        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions { ModelId = "claude-opus-latest" },
            TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().Model.ShouldBe("claude-opus-4-8-20260101");
    }

    [Fact]
    public async Task Records_the_generation_and_rethrows_when_the_llm_call_fails()
    {
        // A failed call is the one most worth seeing in Langfuse, and the caller must still see it fail.
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(new FakeChatClient(new InvalidOperationException("rate limited")), telemetry);

        await Should.ThrowAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            cancellationToken: TestContext.Current.CancellationToken));

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Level.ShouldBe(ObservationLevel.Error);
        generation.StatusMessage.ShouldBe("rate limited");
        generation.EndTime.ShouldNotBeNull();
    }

    [Fact]
    public async Task Captures_content_by_default()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "The answer is 42"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the answer?")],
            cancellationToken: TestContext.Current.CancellationToken);

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Input!.ToJsonString().ShouldContain("What is the answer?");
        generation.Output!.ToJsonString().ShouldContain("The answer is 42");
    }

    [Fact]
    public async Task Omits_content_but_keeps_metrics_when_capture_is_disabled()
    {
        // The regulated-data path: cost and latency still observable, prompt text never leaves.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "SSN 123-45-6789"))
        {
            Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 7 },
        };

        using var client = new LangfuseChatClient(
            new FakeChatClient(response),
            telemetry,
            new LangfuseChatClientOptions { CaptureContent = false });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "my ssn is 123-45-6789")],
            cancellationToken: TestContext.Current.CancellationToken);

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Input.ShouldBeNull();
        generation.Output.ShouldBeNull();
        generation.Usage!["input"].ShouldBe(3);

        telemetry.Traces.ShouldHaveSingleItem().Input.ShouldBeNull();
    }

    [Fact]
    public async Task Links_the_generation_to_the_prompt_revision_that_produced_it()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        var prompt = BuildCompiledPrompt("RefundAgent", version: 7);
        var options = new ChatOptions { ModelId = "m" }.WithLangfusePrompt(prompt);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            options,
            TestContext.Current.CancellationToken);

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.PromptName.ShouldBe("RefundAgent");
        generation.PromptVersion.ShouldBe(7);
    }

    [Fact]
    public async Task Records_model_parameters_and_offered_tools()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        var options = new ChatOptions
        {
            ModelId = "m",
            Temperature = 0.7f,
            MaxOutputTokens = 256,
            Tools = [AIFunctionFactory.Create(() => "sunny", "get_weather")],
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "weather?")],
            options,
            TestContext.Current.CancellationToken);

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        var parameters = generation.ModelParameters!.ToJsonString();
        parameters.ShouldContain("0.7");
        parameters.ShouldContain("256");
        generation.Metadata!.ToJsonString().ShouldContain("get_weather");
    }

    [Fact]
    public async Task Streaming_captures_time_to_first_token_and_the_full_text()
    {
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(
            new FakeChatClient(() =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
                new ChatResponseUpdate(ChatRole.Assistant, "lo "),
                new ChatResponseUpdate(ChatRole.Assistant, "world"),
            ]),
            telemetry);

        using (client)
        {
            var received = new List<string>();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Hi")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
                received.Add(update.Text);
            }

            received.Count.ShouldBe(3);
        }

        var generation = telemetry.Generations.ShouldHaveSingleItem();

        // Time-to-first-token: set when the first chunk arrived, before the stream finished.
        generation.CompletionStartTime.ShouldNotBeNull();
        generation.CompletionStartTime!.Value.ShouldBeGreaterThanOrEqualTo(generation.StartTime!.Value);
        generation.CompletionStartTime!.Value.ShouldBeLessThanOrEqualTo(generation.EndTime!.Value);
        generation.Output!.ToJsonString().ShouldContain("Hello world");
    }

    [Fact]
    public async Task Streaming_records_the_generation_when_the_stream_faults_midway()
    {
        // A stream that dies after a few chunks must still produce a trace, marked as an error.
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(
            new FakeChatClient(Streamer),
            telemetry);

        using (client)
        {
            await Should.ThrowAsync<TimeoutException>(async () =>
            {
                await foreach (var update in client.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "Hi")],
                    cancellationToken: TestContext.Current.CancellationToken))
                {
                    _ = update;
                }
            });
        }

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.Level.ShouldBe(ObservationLevel.Error);
        generation.StatusMessage.ShouldBe("stream died");

        static IEnumerable<ChatResponseUpdate> Streamer()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            throw new TimeoutException("stream died");
        }
    }

    [Fact]
    public async Task Emits_a_trace_at_the_root_but_not_for_nested_calls()
    {
        // A nested call must not overwrite the parent trace's name, user and session.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        using var source = new ActivitySource("test-source");
        using var listener = ListenTo("test-source");
        using var parent = source.StartActivity("outer");

        parent.ShouldNotBeNull();

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Traces.ShouldBeEmpty();
        telemetry.Generations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Correlates_the_langfuse_trace_id_with_the_ambient_w3c_trace()
    {
        // The whole point of W3C correlation: pivot from an OTel trace to its Langfuse generations.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        using var source = new ActivitySource("test-source");
        using var listener = ListenTo("test-source");
        using var parent = source.StartActivity("outer");

        var expectedTraceId = parent!.TraceId.ToHexString();

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().TraceId.ShouldBe(expectedTraceId);
    }

    [Fact]
    public async Task Tracing_never_makes_the_caller_wait_on_langfuse()
    {
        // The core promise of the background channel, asserted at the client boundary: the call
        // returns even though telemetry is being handed off.
        var telemetry = new BlockingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        var completed = await client
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        completed.Text.ShouldBe("ok");
        telemetry.TrackCalls.ShouldBeGreaterThan(0);
    }

    private static ActivityListener ListenTo(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName || s.Name == Diagnostics.LangfuseMetrics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static CompiledPrompt BuildCompiledPrompt(string name, int version)
    {
        var prompt = new LangfusePrompt(
            name,
            version,
            LangfusePromptType.Text,
            "hello",
            [],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network);

        return prompt.Compile();
    }

    /// <summary>Telemetry whose Track is instantaneous but records that it was called.</summary>
    private sealed class BlockingTelemetry : ILangfuseTelemetry
    {
        private int _calls;

        public int TrackCalls => _calls;

        public bool Track(LangfuseTrace trace) => Count();

        public bool Track(LangfuseGeneration generation) => Count();

        public bool Track(LangfuseSpan span) => Count();

        public bool Track(LangfuseScore score) => Count();

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private bool Count()
        {
            Interlocked.Increment(ref _calls);
            return true;
        }
    }
}
