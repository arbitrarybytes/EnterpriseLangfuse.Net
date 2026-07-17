using EnterpriseLangfuse.Extensions.AI.Tests.Infrastructure;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnterpriseLangfuse.Extensions.AI.Tests;

/// <summary>Covers the surface a consumer actually wires up.</summary>
public sealed class ChatClientIntegrationTests
{
    [Fact]
    public async Task UseLangfuse_traces_calls_through_a_built_pipeline()
    {
        var telemetry = new RecordingTelemetry();
        var services = new ServiceCollection();
        services.AddSingleton<ILangfuseTelemetry>(telemetry);

        var inner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")) { ModelId = "m" });

        using var provider = services.BuildServiceProvider();
        using var client = new ChatClientBuilder(inner)
            .UseLangfuse(o => o.OperationName = "support-agent")
            .Build(provider);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Generations.ShouldHaveSingleItem().Name.ShouldBe("support-agent");
    }

    [Fact]
    public void UseLangfuse_rejects_a_null_builder()
    {
        Should.Throw<ArgumentNullException>(() => ((ChatClientBuilder)null!).UseLangfuse());
    }

    [Fact]
    public async Task Resolves_user_and_session_per_call_rather_than_per_registration()
    {
        // The client is a singleton but the user changes per request, so the accessors must be
        // consulted at call time — a captured value would attribute every trace to one user.
        var telemetry = new RecordingTelemetry();
        var currentUser = "alice";

        using var client = new LangfuseChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))),
            telemetry,
            new LangfuseChatClientOptions
            {
                UserIdAccessor = () => currentUser,
                SessionIdAccessor = () => $"session-for-{currentUser}",
                Tags = { "support" },
            });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        currentUser = "bob";
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        telemetry.Traces.Select(t => t.UserId).ShouldBe(["alice", "bob"]);
        telemetry.Traces[1].SessionId.ShouldBe("session-for-bob");
        telemetry.Traces[0].Tags.ShouldBe(["support"]);
    }

    [Fact]
    public void Text_prompt_becomes_a_single_user_message()
    {
        var messages = BuildPrompt(LangfusePromptType.Text, "Hello Ada").ToChatMessages();

        var message = messages.ShouldHaveSingleItem();
        message.Role.ShouldBe(ChatRole.User);
        message.Text.ShouldBe("Hello Ada");
    }

    [Fact]
    public void Chat_prompt_roles_map_onto_meai_roles()
    {
        var prompt = new LangfusePrompt(
            "P",
            1,
            LangfusePromptType.Chat,
            null,
            [
                new LangfuseChatMessage("system", "s"),
                new LangfuseChatMessage("assistant", "a"),
                new LangfuseChatMessage("tool", "t"),
                new LangfuseChatMessage("user", "u"),
            ],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network).Compile();

        prompt.ToChatMessages().Select(m => m.Role)
            .ShouldBe([ChatRole.System, ChatRole.Assistant, ChatRole.Tool, ChatRole.User]);
    }

    [Fact]
    public void An_unrecognised_role_falls_back_to_user_rather_than_failing()
    {
        // A prompt authored in Langfuse with a custom role should still be sendable.
        var prompt = new LangfusePrompt(
            "P",
            1,
            LangfusePromptType.Chat,
            null,
            [new LangfuseChatMessage("developer", "d")],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network).Compile();

        prompt.ToChatMessages().ShouldHaveSingleItem().Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void ToChatMessages_rejects_null()
    {
        Should.Throw<ArgumentNullException>(() => ((CompiledPrompt)null!).ToChatMessages());
    }

    [Fact]
    public async Task A_prompt_flows_end_to_end_from_provider_to_traced_call()
    {
        // The whole product in one test: compile a prompt, send it, and have the generation come
        // back linked to the exact revision that produced it.
        var telemetry = new RecordingTelemetry();
        var inner = new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Refunded")));
        using var client = new LangfuseChatClient(inner, telemetry);

        var prompt = BuildPrompt(LangfusePromptType.Chat, "Customer {{name}} wants a refund", version: 3);
        var options = new ChatOptions { ModelId = "claude-opus-4-8" }.WithLangfusePrompt(prompt);

        await client.GetResponseAsync(prompt.ToChatMessages(), options, TestContext.Current.CancellationToken);

        inner.LastMessages.ShouldHaveSingleItem().Text.ShouldBe("Customer Ada wants a refund");

        var generation = telemetry.Generations.ShouldHaveSingleItem();
        generation.PromptName.ShouldBe("RefundAgent");
        generation.PromptVersion.ShouldBe(3);
        generation.Output!.ToJsonString().ShouldContain("Refunded");
    }

    [Fact]
    public void WithLangfusePrompt_rejects_nulls()
    {
        Should.Throw<ArgumentNullException>(() => ((ChatOptions)null!).WithLangfusePrompt(BuildPrompt(LangfusePromptType.Text, "x")));
        Should.Throw<ArgumentNullException>(() => new ChatOptions().WithLangfusePrompt(null!));
    }

    private static CompiledPrompt BuildPrompt(LangfusePromptType type, string body, int version = 1)
    {
        var prompt = new LangfusePrompt(
            "RefundAgent",
            version,
            type,
            type == LangfusePromptType.Text ? body : null,
            type == LangfusePromptType.Chat ? [new LangfuseChatMessage("user", body)] : [],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network);

        return prompt.Variables.Count > 0
            ? prompt.Compile(new Dictionary<string, object?> { ["name"] = "Ada" })
            : prompt.Compile();
    }
}
