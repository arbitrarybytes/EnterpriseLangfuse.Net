using System.Net;
using BenchmarkDotNet.Attributes;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseLangfuse.Benchmarks;

/// <summary>
/// Substantiates the README's central telemetry claim: recording an observation never blocks the
/// caller on Langfuse.
/// </summary>
/// <remarks>
/// Three arms, because one comparison alone would mislead:
/// <list type="bullet">
/// <item>
/// <b>Channel</b> — what an application pays: serialise and enqueue.
/// </item>
/// <item>
/// <b>Synchronous, instant server</b> — the same work dispatched inline against a handler that
/// returns immediately. This arm exists to be fair: it shows the channel is <em>not</em> a CPU
/// optimisation. Both do the same serialisation, so the costs are comparable and the channel wins
/// only on allocations. Quoting a speedup from this arm would be dishonest.
/// </item>
/// <item>
/// <b>Synchronous, realistic server</b> — the same call against a handler with a
/// <see cref="RealisticLatency"/> round trip, which is what talking to Langfuse Cloud actually costs.
/// This is the arm that shows the real claim: the caller's latency is the network, and the channel
/// removes it from the request path entirely.
/// </item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class TelemetryBenchmarks
{
    /// <summary>
    /// Stand-in for a Langfuse Cloud round trip. Conservative: a cross-region HTTPS call to a
    /// SaaS ingestion endpoint is routinely slower than this.
    /// </summary>
    private static readonly TimeSpan RealisticLatency = TimeSpan.FromMilliseconds(10);

    private LangfuseTelemetryChannel _channel = null!;
    private LangfuseApi _instantApi = null!;
    private LangfuseApi _realisticApi = null!;
    private LangfuseGeneration _generation = null!;
    private Task _drain = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new PromptPipelineBenchmarks.StaticOptions(new LangfuseOptions
        {
            PublicKey = "pk",
            SecretKey = "sk",
            // Large enough that the benchmark measures enqueue cost, not the drop path.
            TelemetryQueueCapacity = 1_000_000,
        });

        _channel = new LangfuseTelemetryChannel(options, TimeProvider.System, NullLogger<LangfuseTelemetryChannel>.Instance);

        _instantApi = new LangfuseApi(
            new HttpClient(new StubServer(TimeSpan.Zero)) { BaseAddress = new Uri("https://langfuse.test/") },
            NullLogger<LangfuseApi>.Instance);

        _realisticApi = new LangfuseApi(
            new HttpClient(new StubServer(RealisticLatency)) { BaseAddress = new Uri("https://langfuse.test/") },
            NullLogger<LangfuseApi>.Instance);

        _generation = new LangfuseGeneration
        {
            Name = "chat",
            Model = "claude-opus-4-8",
            TraceId = TraceIdentifier.New(),
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMilliseconds(842),
            Usage = new Dictionary<string, long> { ["input"] = 1200, ["output"] = 350, ["total"] = 1550 },
            PromptName = "RefundAgent",
            PromptVersion = 3,
        };

        // A background consumer drains the queue continuously, exactly as the real dispatcher does.
        // This replaces an [IterationCleanup] drain, which would have been a measurement trap: BDN
        // forces InvocationCount=1 when a per-iteration hook exists, so every reported "mean" would
        // have been one operation plus iteration overhead — microseconds of harness cost passed off
        // as the price of enqueueing. Draining out-of-band lets BDN unroll and measure the real thing.
        _drain = Task.Run(async () =>
        {
            await foreach (var _ in _channel.EventReader.ReadAllAsync())
            {
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _channel.Complete();
        _drain.GetAwaiter().GetResult();
    }

    /// <summary>
    /// What an application actually pays to record an LLM call: serialise and enqueue. No network.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Track generation (background channel)")]
    public bool TrackViaChannel() => _channel.Track(_generation);

    /// <summary>
    /// Inline dispatch against a server with no latency. Isolates pure CPU/allocation cost, and is
    /// expected to land close to the channel — the honest control for this comparison.
    /// </summary>
    [Benchmark(Description = "Track generation (synchronous, 0ms server — CPU only)")]
    public async Task<int> TrackSynchronouslyInstant() => await DispatchAsync(_instantApi);

    /// <summary>
    /// Inline dispatch against a server with a realistic round trip: what a caller actually waits for
    /// when telemetry is not moved off the request path.
    /// </summary>
    [Benchmark(Description = "Track generation (synchronous, 10ms server — realistic)")]
    public async Task<int> TrackSynchronouslyRealistic() => await DispatchAsync(_realisticApi);

    private async Task<int> DispatchAsync(LangfuseApi api)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToNode(
            TelemetryMapperAccess.ToBody(_generation),
            LangfuseJsonContext.Default.GenerationBodyDto);

        var result = await api.IngestAsync(
            [new IngestionEventDto
            {
                Id = TraceIdentifier.New(),
                Type = IngestionEventTypes.GenerationCreate,
                Timestamp = DateTimeOffset.UtcNow,
                Body = body,
            }],
            CancellationToken.None);

        return result.SuccessCount;
    }

    /// <summary>An HTTP handler that returns success after a fixed delay.</summary>
    private sealed class StubServer(TimeSpan latency) : HttpMessageHandler
    {
        private static readonly byte[] Body = "{\"successes\":[],\"errors\":[]}"u8.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (latency > TimeSpan.Zero)
            {
                await Task.Delay(latency, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
        }
    }
}

/// <summary>Exposes the internal mapper so the synchronous arm does the same work as the channel.</summary>
internal static class TelemetryMapperAccess
{
    public static GenerationBodyDto ToBody(LangfuseGeneration generation) =>
        TelemetryMapper.ToBody(generation, new LangfuseOptions { PublicKey = "pk", SecretKey = "sk" });
}
