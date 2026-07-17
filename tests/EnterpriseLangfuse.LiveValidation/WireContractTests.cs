using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Shouldly;

namespace EnterpriseLangfuse.LiveValidation;

/// <summary>
/// Proves the hand-written wire contracts match the real Langfuse API.
/// </summary>
/// <remarks>
/// This suite exists because <c>EnterpriseLangfuse.Core</c> replaced the AutoSDK's models with its
/// own (the SDK's <c>AllOf</c> converter drops payload bodies). Every other test in this repo stubs
/// HTTP against those same assumptions, so none of them can catch a wrong field name — the request
/// would be built wrong, parsed wrong, and pass. Only a real API can.
/// <para>
/// Reads are verified with raw <see cref="JsonDocument"/> against the field names Langfuse documents,
/// never through this library's own deserialisation.
/// </para>
/// </remarks>
[Collection(LangfuseCollection.Name)]
public sealed class WireContractTests(LangfuseFixture langfuse)
{
    /// <summary>
    /// How long to wait for an accepted event to become queryable.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Langfuse ingestion is asynchronous: a 201 means "queued", not "visible".
    /// Measured against Langfuse Cloud, a trace took ~33s to appear, so a 60s budget leaves almost no
    /// headroom for a slow day and would produce exactly the kind of flaky suite people learn to
    /// ignore. Failing here should mean "the event never arrived", not "the backend was busy".
    /// </remarks>
    private static readonly TimeSpan IngestionTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Text_prompt_round_trips_as_text()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        var name = $"live-text-{langfuse.RunId}";
        var token = TestContext.Current.CancellationToken;

        await CreatePromptAsync(
            new
            {
                name,
                type = "text",
                prompt = "Hello {{customerName}}, your order {{orderId}} shipped.",
                labels = new[] { "production" },
                config = new { model = "claude-opus-4-8", temperature = 0.2 },
            },
            token);

        var prompt = await langfuse.Prompts.GetPromptAsync(name, cancellationToken: token);

        // The exact defect this library routes around: the AutoSDK reports text prompts as chat.
        prompt.Type.ShouldBe(LangfusePromptType.Text);
        prompt.Source.ShouldBe(PromptSource.Network);
        prompt.Text.ShouldBe("Hello {{customerName}}, your order {{orderId}} shipped.");
        prompt.Version.ShouldBeGreaterThan(0);
        prompt.Labels.ShouldContain("production");
        prompt.Variables.ShouldBe(["customerName", "orderId"]);
        prompt.Config["model"].ShouldBe("claude-opus-4-8");

        prompt.Compile(new Dictionary<string, object?> { ["customerName"] = "Ada", ["orderId"] = "A-42" })
            .Text.ShouldBe("Hello Ada, your order A-42 shipped.");
    }

    [Fact]
    public async Task Chat_prompt_round_trips_with_its_messages()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        var name = $"live-chat-{langfuse.RunId}";
        var token = TestContext.Current.CancellationToken;

        await CreatePromptAsync(
            new
            {
                name,
                type = "chat",
                prompt = new[]
                {
                    new { role = "system", content = "You are a refund agent." },
                    new { role = "user", content = "Customer {{customerName}} asks about {{orderId}}." },
                },
                labels = new[] { "production" },
            },
            token);

        var prompt = await langfuse.Prompts.GetPromptAsync(name, cancellationToken: token);

        // Under the AutoSDK this body is unreachable — Value2 comes back null.
        prompt.Type.ShouldBe(LangfusePromptType.Chat);
        prompt.Messages.Count.ShouldBe(2);
        prompt.Messages[0].Role.ShouldBe("system");
        prompt.Messages[1].Content.ShouldBe("Customer {{customerName}} asks about {{orderId}}.");
        prompt.Variables.ShouldBe(["customerName", "orderId"]);
    }

    [Fact]
    public async Task Requesting_a_label_resolves_that_label()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        var name = $"live-label-{langfuse.RunId}";
        var token = TestContext.Current.CancellationToken;

        await CreatePromptAsync(new { name, type = "text", prompt = "v1 production", labels = new[] { "production" } }, token);
        await CreatePromptAsync(new { name, type = "text", prompt = "v2 staging", labels = new[] { "staging" } }, token);

        (await langfuse.Prompts.GetPromptAsync(name, "production", token)).Text.ShouldBe("v1 production");
        (await langfuse.Prompts.GetPromptAsync(name, "staging", token)).Text.ShouldBe("v2 staging");
    }

    [Fact]
    public async Task A_missing_prompt_surfaces_as_not_found_rather_than_an_outage()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        // Fallback is disabled on this fixture, so a 404 must propagate as PromptNotFoundException.
        await Should.ThrowAsync<PromptNotFoundException>(() => langfuse.Prompts.GetPromptAsync(
            $"live-does-not-exist-{langfuse.RunId}",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_trace_arrives_with_its_body_intact()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        var token = TestContext.Current.CancellationToken;
        var traceId = TraceIdentifier.New();

        langfuse.Telemetry.Track(new LangfuseTrace
        {
            Id = traceId,
            Name = $"live-trace-{langfuse.RunId}",
            UserId = "user-live-validation",
            SessionId = $"session-{langfuse.RunId}",
            Tags = ["live-validation"],
            Input = System.Text.Json.Nodes.JsonNode.Parse("""{"question":"is this body preserved?"}"""),
        });

        await langfuse.Telemetry.FlushAsync(token);

        var trace = await LangfuseFixture.PollAsync<JsonElement>(
            async ct => await langfuse.GetJsonAsync($"api/public/traces/{traceId}", ct),
            IngestionTimeout,
            token);

        trace.ShouldNotBeNull($"trace {traceId} never appeared in Langfuse within {IngestionTimeout}");

        // The AutoSDK would have sent {"type":"trace-create"} and lost all of this.
        var root = trace.Value;
        root.GetProperty("name").GetString().ShouldBe($"live-trace-{langfuse.RunId}");
        root.GetProperty("userId").GetString().ShouldBe("user-live-validation");
        root.GetProperty("sessionId").GetString().ShouldBe($"session-{langfuse.RunId}");
        root.GetProperty("release").GetString().ShouldBe(langfuse.RunId);
        root.GetProperty("environment").GetString().ShouldBe("live-validation");
        root.GetProperty("input").GetProperty("question").GetString().ShouldBe("is this body preserved?");
    }

    [Fact]
    public async Task A_generation_arrives_with_model_usage_and_its_prompt_link()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        var token = TestContext.Current.CancellationToken;
        var traceId = TraceIdentifier.New();
        var generationId = TraceIdentifier.New();

        langfuse.Telemetry.Track(new LangfuseTrace { Id = traceId, Name = $"live-gen-trace-{langfuse.RunId}" });
        langfuse.Telemetry.Track(new LangfuseGeneration
        {
            Id = generationId,
            TraceId = traceId,
            Name = "live-generation",
            Model = "claude-opus-4-8",
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            EndTime = DateTimeOffset.UtcNow,
            Usage = new Dictionary<string, long> { ["input"] = 1200, ["output"] = 350, ["total"] = 1550 },
            Input = System.Text.Json.Nodes.JsonNode.Parse("""[{"role":"user","content":"hello"}]"""),
            Output = System.Text.Json.Nodes.JsonNode.Parse("""{"role":"assistant","content":"hi there"}"""),
        });

        await langfuse.Telemetry.FlushAsync(token);

        var observation = await LangfuseFixture.PollAsync<JsonElement>(
            async ct => await langfuse.GetJsonAsync($"api/public/observations/{generationId}", ct),
            IngestionTimeout,
            token);

        observation.ShouldNotBeNull($"generation {generationId} never appeared within {IngestionTimeout}");

        var root = observation.Value;
        root.GetProperty("type").GetString().ShouldBe("GENERATION");
        root.GetProperty("name").GetString().ShouldBe("live-generation");
        root.GetProperty("model").GetString().ShouldBe("claude-opus-4-8");
        root.GetProperty("traceId").GetString().ShouldBe(traceId);

        // usageDetails is the field name this library writes; if Langfuse expected something else,
        // token counts would silently read as zero and cost reporting would be wrong.
        var usage = root.GetProperty("usageDetails");
        usage.GetProperty("input").GetInt64().ShouldBe(1200);
        usage.GetProperty("output").GetInt64().ShouldBe(350);
    }

    [Fact]
    public async Task Langfuse_accepts_every_event_type_this_library_emits()
    {
        Assert.SkipUnless(langfuse.IsConfigured, langfuse.SkipReason);

        // A rejected event is reported per-event by the ingestion endpoint rather than failing the
        // request, so a schema mistake would otherwise be invisible.
        var token = TestContext.Current.CancellationToken;
        var traceId = TraceIdentifier.New();

        langfuse.Telemetry.Track(new LangfuseTrace { Id = traceId, Name = $"live-all-{langfuse.RunId}" });
        langfuse.Telemetry.Track(new LangfuseSpan { TraceId = traceId, Name = "live-span" });
        langfuse.Telemetry.Track(new LangfuseGeneration { TraceId = traceId, Name = "live-gen", Model = "claude-opus-4-8" });
        langfuse.Telemetry.Track(new LangfuseScore
        {
            TraceId = traceId,
            Name = "helpfulness",
            Value = System.Text.Json.Nodes.JsonValue.Create(0.9),
            DataType = "NUMERIC",
        });

        await langfuse.Telemetry.FlushAsync(token);

        var trace = await LangfuseFixture.PollAsync<JsonElement>(
            async ct =>
            {
                var root = await langfuse.GetJsonAsync($"api/public/traces/{traceId}", ct);

                // Every child must be in the poll condition, not asserted afterwards. Langfuse makes
                // a trace queryable before all of its children have necessarily attached, so polling
                // on observations alone and then checking scores is a race that fails intermittently.
                var observations = root.TryGetProperty("observations", out var o) ? o.GetArrayLength() : 0;
                var scores = root.TryGetProperty("scores", out var s) ? s.GetArrayLength() : 0;

                return observations >= 2 && scores >= 1 ? root : (JsonElement?)null;
            },
            IngestionTimeout,
            token);

        trace.ShouldNotBeNull(
            $"the span, generation and score never all attached to trace {traceId} within {IngestionTimeout}");

        // Reaching here means Langfuse accepted and materialised every event type this library emits.
        trace!.Value.GetProperty("observations").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
        trace.Value.GetProperty("scores").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Creates a prompt by posting the documented shape directly, bypassing this library.
    /// </summary>
    /// <remarks>
    /// Writing the fixture through our own code would make these tests circular — they must assert
    /// that we read what Langfuse actually stores, not what we chose to send.
    /// </remarks>
    private async Task CreatePromptAsync(object payload, CancellationToken cancellationToken)
    {
        using var response = await langfuse.Verifier.PostAsJsonAsync("api/public/v2/prompts", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue($"creating the prompt failed ({(int)response.StatusCode}): {body}");
    }
}
