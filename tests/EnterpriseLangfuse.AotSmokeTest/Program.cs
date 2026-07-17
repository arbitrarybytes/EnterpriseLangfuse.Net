using System.Net;
using System.Reflection;
using EnterpriseLangfuse;
using EnterpriseLangfuse.Extensions.AI;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Exercises every runtime path that could depend on reflection, under Native AOT.
//
// Merely *linking* an AOT binary proves little: the reflection-based failures this guards against
// (JSON contract discovery, YAML deserialisation, options validation) throw at the moment of use,
// not at startup. So each path below is actually executed and its result asserted.

var failures = new List<string>();

await Check("prompt resolution over the wire (source-generated JSON)", async () =>
{
    var provider = BuildProvider(PromptResponse());
    var prompt = await provider.GetRequiredService<IPromptProvider>().GetPromptAsync("RefundAgent");

    Assert(prompt.Source == PromptSource.Network, $"expected Network, got {prompt.Source}");
    Assert(prompt.Version == 3, $"expected version 3, got {prompt.Version}");
    Assert(prompt.Messages.Count == 2, $"expected 2 messages, got {prompt.Messages.Count}");
});

await Check("offline fallback (YamlDotNet DOM parsing of an embedded resource)", async () =>
{
    // The path most likely to break under AOT: YAML parsing at runtime. Uses YamlDotNet's
    // reflection-free representation model precisely so this survives.
    var provider = BuildProvider(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
    var prompt = await provider.GetRequiredService<IPromptProvider>().GetPromptAsync("RefundAgent");

    Assert(prompt.Source == PromptSource.EmbeddedFallback, $"expected EmbeddedFallback, got {prompt.Source}");
    Assert(prompt.Messages.Count == 2, $"expected 2 embedded messages, got {prompt.Messages.Count}");
});

await Check("prompt compilation", () =>
{
    var provider = BuildProvider(PromptResponse());
    var prompt = provider.GetRequiredService<IPromptProvider>().GetPromptAsync("RefundAgent").GetAwaiter().GetResult();
    var compiled = prompt.Compile(new Dictionary<string, object?> { ["customerName"] = "Ada", ["orderId"] = "A-42" });

    Assert(
        compiled.Messages[1].Content == "Customer Ada is asking about order A-42.",
        $"unexpected render: {compiled.Messages[1].Content}");

    return Task.CompletedTask;
});

await Check("telemetry serialisation and dispatch", async () =>
{
    string? body = null;
    var provider = BuildProvider(request =>
    {
        body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json("""{"successes":[],"errors":[]}""");
    });

    var dispatcher = provider.GetServices<IHostedService>().OfType<BackgroundService>().Single();
    await dispatcher.StartAsync(CancellationToken.None);

    var telemetry = provider.GetRequiredService<ILangfuseTelemetry>();
    telemetry.Track(new LangfuseGeneration { Name = "chat", Model = "claude-opus-4-8" });

    await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await dispatcher.StopAsync(CancellationToken.None);

    Assert(body is not null, "no ingestion request was sent");
    Assert(body!.Contains("generation-create"), $"event type missing from payload: {body}");
    Assert(body.Contains("claude-opus-4-8"), $"model missing from payload — AllOf-style body loss: {body}");
});

await Check("MEAI chat client tracing", async () =>
{
    var provider = BuildProvider(_ => Json("""{"successes":[],"errors":[]}"""));
    var telemetry = provider.GetRequiredService<ILangfuseTelemetry>();

    using var client = new LangfuseChatClient(new EchoChatClient(), telemetry);
    var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

    Assert(response.Text == "echo: hello", $"unexpected response: {response.Text}");
});

await Check("options validation (reflection-free)", () =>
{
    var services = new ServiceCollection();
    services.AddEnterpriseLangfuse(o => o.PublicKey = "pk-only");

    using var sp = services.BuildServiceProvider();
    try
    {
        _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LangfuseOptions>>().Value;
        Assert(false, "expected validation to reject the missing SecretKey");
    }
    catch (Microsoft.Extensions.Options.OptionsValidationException ex)
    {
        Assert(ex.Message.Contains("SecretKey"), $"unexpected validation message: {ex.Message}");
    }

    return Task.CompletedTask;
});

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"AOT SMOKE TEST FAILED ({failures.Count} of 6 checks):");
    failures.ForEach(f => Console.WriteLine("  - " + f));
    return 1;
}

// Deliberately does not claim "ran natively": the same program is published both trimmed and as
// Native AOT, and only the publish mode knows which. Overstating that here would turn a verification
// tool into a source of false confidence.
Console.WriteLine("AOT SMOKE TEST PASSED: all 6 checks executed successfully.");
return 0;

async Task Check(string name, Func<Task> action)
{
    try
    {
        await action();
        Console.WriteLine($"  [ ok ] {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
        failures.Add($"{name}: {ex.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static Func<HttpRequestMessage, HttpResponseMessage> PromptResponse() => _ => Json(
    """
    {"name":"RefundAgent","version":3,"type":"chat","labels":["production"],
     "config":{"model":"claude-opus-4-8","temperature":0.2},
     "prompt":[{"role":"system","content":"You are a refund agent."},
               {"role":"user","content":"Customer {{customerName}} is asking about order {{orderId}}."}]}
    """);

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
};

static ServiceProvider BuildProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
{
    var services = new ServiceCollection();

    services.AddEnterpriseLangfuse(o =>
    {
        o.PublicKey = "pk-lf-aot";
        o.SecretKey = "sk-lf-aot";
        o.BaseUrl = new Uri("https://langfuse.test");
        o.TelemetryFlushInterval = TimeSpan.FromMilliseconds(20);
        o.FallbackAssemblies.Add(Assembly.GetExecutingAssembly());
    });

    services.AddHttpClient("EnterpriseLangfuse")
        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(responder));

    return services.BuildServiceProvider();
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"echo: {messages.Last().Text}"))
        {
            ModelId = "echo-model",
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2, TotalTokenCount = 3 },
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "echo");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
