using EnterpriseLangfuse;
using EnterpriseLangfuse.Extensions.AI;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RefundAgent;

// A runnable tour of EnterpriseLangfuse.NET.
//
// It runs with no configuration at all: without Langfuse credentials it points at an unreachable
// host, which is not a degraded demo but the point — scenario 2 shows the application still serving
// prompts through a total outage. Supply credentials (see README.md) and the same code paths talk to
// real Langfuse instead.

var credentials = LangfuseCredentials.Discover();
credentials.PrintBanner();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("EnterpriseLangfuse", LogLevel.Information);

// Polly logs each retry at Warning with a full stack trace. During scenario 2 that is a dozen
// screens of expected noise for a failure the pipeline is designed to absorb. Silencing it here is a
// sample-readability choice — in production you want these, but you probably want them at Debug, or
// scoped to a Langfuse-specific logger, so a telemetry backend hiccup cannot bury real warnings.
builder.Logging.AddFilter("Polly", LogLevel.None);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);

builder.Services.AddEnterpriseLangfuse(options =>
{
    options.PublicKey = credentials.PublicKey;
    options.SecretKey = credentials.SecretKey;
    options.BaseUrl = credentials.BaseUrl;
    options.Environment = "sample";
    options.Release = "1.0.0";

    // Short so the tour finishes promptly; the shipped default is 5s.
    options.TelemetryFlushInterval = TimeSpan.FromMilliseconds(250);

    // Without credentials the base URL is unreachable, and waiting out the default 10s timeout
    // three times would make the fallback look slow when it is actually instant.
    options.RequestTimeout = credentials.AreReal ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);
});

// A real application would register a provider here, e.g.
//   builder.Services.AddChatClient(new AnthropicChatClient(apiKey))
// The canned client keeps this sample runnable without an LLM key; everything downstream is identical.
builder.Services.AddChatClient(new CannedChatClient())
                .UseLangfuse(o =>
                {
                    o.OperationName = "refund-agent";
                    o.UserIdAccessor = () => "customer-1024";
                    o.Tags.Add("sample");
                });

using var host = builder.Build();
await host.StartAsync();

var prompts = host.Services.GetRequiredService<IPromptProvider>();
var chat = host.Services.GetRequiredService<IChatClient>();
var telemetry = host.Services.GetRequiredService<ILangfuseTelemetry>();

// ---------------------------------------------------------------------------
Section(1, "Typed prompt → traced LLM call");

// GetRefundAgentPromptAsync is generated from Prompts/RefundAgent.prompt.yaml.
// Its parameters ARE the prompt's {{variables}} — delete one and this stops compiling.
var prompt = await prompts.GetRefundAgentPromptAsync(
    customerName: "Ada Lovelace",
    orderId: "A-4815",
    question: "My order arrived damaged. Can I get a refund?");

Console.WriteLine($"  prompt   : {prompt.Name} v{prompt.Version}  (source: {prompt.Source})");
Console.WriteLine($"  rendered : {Truncate(prompt.Messages[^1].Content)}");

var options = new ChatOptions { ModelId = "claude-opus-4-8", Temperature = 0.2f }
    .WithLangfusePrompt(prompt);   // links the generation to this exact prompt revision

var response = await chat.GetResponseAsync(prompt.ToChatMessages(), options);
Console.WriteLine($"  model    : {Truncate(response.Text)}");
Console.WriteLine($"  tokens   : {response.Usage?.TotalTokenCount} total");

// ---------------------------------------------------------------------------
Section(2, "Zero-downtime: the same call while Langfuse is unreachable");

if (credentials.AreReal)
{
    Console.WriteLine("  Credentials are configured, so this run resolved from Langfuse itself.");
    Console.WriteLine("  Re-run without them (or block the host) to watch the fallback engage.");
}

// A second resolve under whatever conditions this run has. With no credentials the host does not
// resolve, the pipeline exhausts its retries, and the embedded YAML is served instead.
var underOutage = await prompts.GetPromptAsync("RefundAgent");

Console.WriteLine($"  source   : {underOutage.Source}");
Console.WriteLine(underOutage.Source == PromptSource.EmbeddedFallback
    ? "  ✔ Langfuse is unreachable and the application is still serving prompts.\n" +
      "    Source reports EmbeddedFallback, so you can alarm on running degraded."
    : "  ✔ Served live from Langfuse.");

// ---------------------------------------------------------------------------
Section(3, "Telemetry never blocks the caller");

var before = System.Diagnostics.Stopwatch.GetTimestamp();
for (var i = 0; i < 1_000; i++)
{
    telemetry.Track(new LangfuseGeneration
    {
        Name = "bulk-sample",
        Model = "claude-opus-4-8",
        Usage = new Dictionary<string, long> { ["input"] = 10, ["output"] = 20, ["total"] = 30 },
    });
}

var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(before);
Console.WriteLine($"  recorded 1,000 generations in {elapsed.TotalMilliseconds:F1} ms " +
                  $"({elapsed.TotalMicroseconds / 1000:F1} µs each) — no network on this path");

// FlushAsync is for short-lived processes like this one. A long-running service never calls it;
// the background dispatcher drains the queue on its own.
using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    await telemetry.FlushAsync(flushTimeout.Token);
    Console.WriteLine(credentials.AreReal
        ? "  flushed to Langfuse — check the Traces view."
        : "  flush attempted; delivery failed (no Langfuse), which never reached the caller.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("  flush timed out — expected without a reachable Langfuse.");
}

Console.WriteLine();
Console.WriteLine(credentials.AreReal
    ? "Done. Open your Langfuse project to see the traces, prompt link and token usage."
    : "Done. Set LANGFUSE_PUBLIC_KEY / LANGFUSE_SECRET_KEY to send this to a real Langfuse.");

await host.StopAsync();   // drains queued telemetry, time-boxed
return 0;

static void Section(int number, string title)
{
    Console.WriteLine();
    Console.WriteLine($"── {number}. {title} ".PadRight(78, '─'));
}

static string Truncate(string? value, int max = 96)
{
    if (string.IsNullOrEmpty(value))
    {
        return "(empty)";
    }

    var single = value.ReplaceLineEndings(" ").Trim();
    return single.Length <= max ? single : single[..max] + "…";
}
