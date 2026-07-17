using EnterpriseLangfuse.Extensions.AI.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Shouldly;

namespace EnterpriseLangfuse.Extensions.AI.Tests;

/// <summary>
/// Covers the projection onto the shape Langfuse's UI renders. Exercised through the public client
/// rather than the internal serialiser, so these assert what actually reaches Langfuse.
/// </summary>
public sealed class ChatPayloadSerializerTests
{
    [Fact]
    public async Task Surfaces_tool_calls_that_would_otherwise_look_like_empty_messages()
    {
        // An assistant turn that only calls a tool has no text. Without this it would appear in
        // Langfuse as a blank message, hiding the most debuggable part of an agent trace.
        var telemetry = new RecordingTelemetry();
        var assistant = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_weather")]);

        using var client = new LangfuseChatClient(new FakeChatClient(new ChatResponse(assistant)), telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "weather?")],
            cancellationToken: TestContext.Current.CancellationToken);

        var output = telemetry.Generations.ShouldHaveSingleItem().Output!.ToJsonString();
        output.ShouldContain("tool_calls");
        output.ShouldContain("get_weather");
        output.ShouldContain("call-1");
    }

    [Fact]
    public async Task Records_the_author_name_when_present()
    {
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi") { AuthorName = "ada" }],
            cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().Input!.ToJsonString().ShouldContain("ada");
    }

    [Fact]
    public async Task Renders_a_multi_message_response_as_an_array()
    {
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, "first"),
            new ChatMessage(ChatRole.Assistant, "second"),
        ]);

        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        var output = telemetry.Generations.ShouldHaveSingleItem().Output!;
        output.ShouldBeOfType<System.Text.Json.Nodes.JsonArray>();
        output.ToJsonString().ShouldContain("second");
    }

    [Fact]
    public async Task An_empty_message_list_records_no_input()
    {
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry);

        await client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().Input.ShouldBeNull();
    }

    [Fact]
    public async Task Captures_cached_and_reasoning_tokens_which_are_billed_separately()
    {
        // Dropping these would misstate cost, which is one of the main reasons to trace at all.
        var telemetry = new RecordingTelemetry();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                TotalTokenCount = 150,
                CachedInputTokenCount = 80,
                ReasoningTokenCount = 20,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["audio"] = 7 },
            },
        };

        using var client = new LangfuseChatClient(new FakeChatClient(response), telemetry);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        var usage = telemetry.Generations.ShouldHaveSingleItem().Usage!;
        usage["input_cached"].ShouldBe(80);
        usage["output_reasoning"].ShouldBe(20);
        usage["audio"].ShouldBe(7);
    }

    [Fact]
    public async Task A_response_without_usage_records_none_rather_than_zeros()
    {
        // Reporting zero tokens would silently understate cost in Langfuse's aggregates.
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().Usage.ShouldBeNull();
    }

    [Fact]
    public async Task Streaming_falls_back_to_accumulated_text_when_updates_carry_no_structure()
    {
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(
            new FakeChatClient(() => [new ChatResponseUpdate(null, "raw chunk")]),
            telemetry);

        using (client)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }

        telemetry.Generations.ShouldHaveSingleItem().Output!.ToJsonString().ShouldContain("raw chunk");
    }

    [Fact]
    public async Task Streaming_with_no_updates_records_no_output()
    {
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(new FakeChatClient(Array.Empty<ChatResponseUpdate>), telemetry);

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

        // Nothing ever streamed, so there is no time-to-first-token to report.
        generation.CompletionStartTime.ShouldBeNull();
    }

    [Fact]
    public async Task Streaming_omits_content_when_capture_is_disabled()
    {
        var telemetry = new RecordingTelemetry();
        var client = new LangfuseChatClient(
            new FakeChatClient(() => [new ChatResponseUpdate(ChatRole.Assistant, "secret")]),
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
        generation.Input.ShouldBeNull();
        generation.Output.ShouldBeNull();
    }

    [Fact]
    public async Task Rejects_null_messages()
    {
        var telemetry = new RecordingTelemetry();
        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry);

        await Should.ThrowAsync<ArgumentNullException>(
            () => client.GetResponseAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Rejects_null_telemetry()
    {
        Should.Throw<ArgumentNullException>(() => new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            telemetry: null!));
    }
}
