using System.Net;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Core.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Api;

/// <summary>
/// Covers the wire mapping this library owns because the AutoSDK's generated models cannot
/// round-trip these payloads.
/// </summary>
public sealed class LangfuseApiTests
{
    [Fact]
    public async Task Maps_a_chat_prompt()
    {
        var prompt = await GetPrompt(
            """
            {"name":"RefundAgent","version":3,"type":"chat","labels":["production"],"tags":["support"],
             "config":{"model":"claude-opus-4-8","temperature":0.2,"stream":true,"retries":3,"stop":null},
             "prompt":[{"role":"system","content":"You are helpful"},{"role":"user","content":"Hi {{name}}"}]}
            """);

        prompt!.Type.ShouldBe(LangfusePromptType.Chat);
        prompt.Version.ShouldBe(3);
        prompt.Messages.Count.ShouldBe(2);
        prompt.Labels.ShouldBe(["production"]);
        prompt.Tags.ShouldBe(["support"]);
        prompt.Variables.ShouldBe(["name"]);
        prompt.Config["model"].ShouldBe("claude-opus-4-8");
        prompt.Config["temperature"].ShouldBe(0.2);
        prompt.Config["stream"].ShouldBe(true);
        prompt.Config["retries"].ShouldBe(3L);
        prompt.Config["stop"].ShouldBeNull();
        prompt.Source.ShouldBe(PromptSource.Network);
    }

    [Fact]
    public async Task Maps_a_text_prompt()
    {
        var prompt = await GetPrompt("""{"name":"Greeter","version":1,"type":"text","prompt":"Hello {{name}}"}""");

        prompt!.Type.ShouldBe(LangfusePromptType.Text);
        prompt.Text.ShouldBe("Hello {{name}}");
    }

    [Fact]
    public async Task Infers_the_type_from_the_body_when_langfuse_omits_it()
    {
        // Older Langfuse payloads carry no `type`. The AutoSDK's union always guesses chat here,
        // which is exactly the defect this mapper exists to avoid.
        (await GetPrompt("""{"name":"P","version":1,"prompt":"just text"}"""))!
            .Type.ShouldBe(LangfusePromptType.Text);

        (await GetPrompt("""{"name":"P","version":1,"prompt":[{"role":"user","content":"hi"}]}"""))!
            .Type.ShouldBe(LangfusePromptType.Chat);
    }

    [Fact]
    public async Task Skips_placeholder_messages_rather_than_failing_the_whole_fetch()
    {
        // Langfuse's message-list placeholders have no literal content; a prompt that uses one
        // should still be usable.
        var prompt = await GetPrompt(
            """
            {"name":"P","version":1,"type":"chat",
             "prompt":[{"role":"user","content":"hi"},{"type":"placeholder","name":"history"}]}
            """);

        prompt!.Messages.ShouldHaveSingleItem().Content.ShouldBe("hi");
    }

    [Fact]
    public async Task Returns_null_for_a_prompt_langfuse_says_does_not_exist()
    {
        // A 404 is authoritative, not transient, and must be distinguishable from an outage.
        var api = Build(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        (await api.GetPromptAsync("Missing", "production", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Surfaces_a_failed_status_as_a_transport_error(HttpStatusCode status)
    {
        var api = Build(StubHttpMessageHandler.AlwaysFails(status));

        await Should.ThrowAsync<HttpRequestException>(
            () => api.GetPromptAsync("P", "production", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"name":"P","version":1,"type":"text","prompt":[{"role":"user","content":"x"}]}""", "not a string")]
    [InlineData("""{"name":"P","version":1,"type":"chat","prompt":"text body"}""", "not an array")]
    [InlineData("""{"name":"P","version":1,"type":"chat","prompt":["not an object"]}""", "not an object")]
    [InlineData("""{"name":"P","version":1,"type":"text"}""", "no body")]
    public async Task Rejects_a_payload_whose_body_contradicts_its_type(string json, string expected)
    {
        var api = Build(StubHttpMessageHandler.AlwaysReturnsJson(json));

        var error = await Should.ThrowAsync<LangfuseApiException>(
            () => api.GetPromptAsync("P", "production", TestContext.Current.CancellationToken));

        error.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task Rejects_a_body_that_is_not_json()
    {
        var api = Build(StubHttpMessageHandler.AlwaysReturnsJson("<html>proxy error</html>"));

        (await Should.ThrowAsync<LangfuseApiException>(
            () => api.GetPromptAsync("P", "production", TestContext.Current.CancellationToken)))
            .Message.ShouldContain("could not be parsed");
    }

    [Fact]
    public async Task Falls_back_to_the_requested_name_when_the_payload_omits_it()
    {
        (await GetPrompt("""{"version":1,"type":"text","prompt":"hi"}"""))!.Name.ShouldBe("Requested");
    }

    [Fact]
    public async Task Ingesting_an_empty_batch_makes_no_request()
    {
        var handler = StubHttpMessageHandler.AlwaysReturnsJson("{}");
        var api = Build(handler);

        var result = await api.IngestAsync([], TestContext.Current.CancellationToken);

        result.SuccessCount.ShouldBe(0);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Reports_per_event_rejections_without_failing_the_batch()
    {
        // Langfuse accepts batches partially; the accepted events are already stored, so retrying
        // the whole batch would duplicate them.
        var api = Build(StubHttpMessageHandler.AlwaysReturnsJson(
            """{"successes":[{"id":"a","status":201}],"errors":[{"id":"b","status":400,"message":"bad body"}]}"""));

        var result = await api.IngestAsync(
            [new IngestionEventDto { Id = "a", Type = "trace-create" }],
            TestContext.Current.CancellationToken);

        result.SuccessCount.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().Message.ShouldBe("bad body");
    }

    private static async Task<LangfusePrompt?> GetPrompt(string json)
    {
        var api = Build(StubHttpMessageHandler.AlwaysReturnsJson(json));
        return await api.GetPromptAsync("Requested", "production", TestContext.Current.CancellationToken);
    }

    private static LangfuseApi Build(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://langfuse.test/") },
            NullLogger<LangfuseApi>.Instance);
}
