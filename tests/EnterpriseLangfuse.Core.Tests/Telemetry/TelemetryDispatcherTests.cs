using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Core.Tests.Prompts;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Telemetry;

public sealed class TelemetryDispatcherTests
{
    [Fact]
    public async Task Dispatches_queued_events_to_langfuse()
    {
        var api = new RecordingApi();
        await using var host = Start(api, out var channel);

        channel.Track(new LangfuseGeneration { Name = "one" });
        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        api.AllEvents.ShouldHaveSingleItem().Type.ShouldBe(IngestionEventTypes.GenerationCreate);
    }

    [Fact]
    public async Task Sends_many_events_as_one_batch_rather_than_one_request_each()
    {
        // Batching is why a high-traffic service does not open a request per span.
        var api = new RecordingApi();
        await using var host = Start(api, out var channel, o => o.TelemetryBatchSize = 100);

        for (var i = 0; i < 50; i++)
        {
            channel.Track(new LangfuseGeneration());
        }

        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        api.AllEvents.Count.ShouldBe(50);
        api.Batches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Splits_a_backlog_across_batches_of_the_configured_size()
    {
        var api = new RecordingApi();
        await using var host = Start(api, out var channel, o => o.TelemetryBatchSize = 10);

        for (var i = 0; i < 25; i++)
        {
            channel.Track(new LangfuseGeneration());
        }

        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        api.AllEvents.Count.ShouldBe(25);
        api.Batches.ShouldAllBe(b => b.Count <= 10);
    }

    [Fact]
    public async Task A_failing_langfuse_never_faults_the_background_service()
    {
        // An unhandled exception in a BackgroundService tears down the host by default. Telemetry
        // must never be able to kill the application it observes.
        var api = new RecordingApi { Failure = new HttpRequestException("langfuse is down") };
        await using var host = Start(api, out var channel);

        channel.Track(new LangfuseGeneration());
        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Still running and still accepting work after the failure.
        host.Service.ExecuteTask!.IsFaulted.ShouldBeFalse();
        channel.Track(new LangfuseGeneration()).ShouldBeTrue();
    }

    [Fact]
    public async Task A_partial_batch_rejection_does_not_fail_the_accepted_events()
    {
        // Langfuse accepts batches partially; retrying the whole batch would duplicate what landed.
        var api = new RecordingApi
        {
            Response = new IngestionResult(1, [new IngestionErrorDto { Id = "x", Status = 400, Message = "bad" }]),
        };

        await using var host = Start(api, out var channel);

        channel.Track(new LangfuseGeneration());
        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        host.Service.ExecuteTask!.IsFaulted.ShouldBeFalse();
        api.Batches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Drains_queued_telemetry_on_shutdown()
    {
        // Without this, every deployment silently loses whatever was still queued.
        var api = new RecordingApi();
        var host = Start(api, out var channel);

        for (var i = 0; i < 5; i++)
        {
            channel.Track(new LangfuseGeneration());
        }

        await host.Service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        api.AllEvents.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Shutdown_gives_up_rather_than_hanging_on_a_dead_langfuse()
    {
        // A drain that waits forever turns an unreachable Langfuse into a stuck deployment.
        var api = new RecordingApi { Failure = new HttpRequestException("down") };
        var host = Start(api, out var channel, o => o.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(50));

        channel.Track(new LangfuseGeneration());

        await host.Service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Flush_completes_even_with_nothing_queued()
    {
        var api = new RecordingApi();
        await using var host = Start(api, out var channel);

        await channel.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        api.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shutdown_releases_a_caller_waiting_on_a_flush()
    {
        // A caller blocked in FlushAsync must not deadlock when the host stops.
        var api = new RecordingApi();
        var host = Start(api, out var channel);

        var flush = channel.FlushAsync(TestContext.Current.CancellationToken);
        await host.Service.StopAsync(TestContext.Current.CancellationToken);

        await flush.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static RunningDispatcher Start(
        ILangfuseApi api,
        out LangfuseTelemetryChannel channel,
        Action<LangfuseOptions>? configure = null)
    {
        var options = new LangfuseOptions
        {
            PublicKey = "pk",
            SecretKey = "sk",
            // Keep the batch window short so tests do not sit on the flush interval.
            TelemetryFlushInterval = TimeSpan.FromMilliseconds(20),
        };
        configure?.Invoke(options);

        var monitor = new StaticOptionsMonitor<LangfuseOptions>(options);
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(5) };

        channel = new LangfuseTelemetryChannel(monitor, time, NullLogger<LangfuseTelemetryChannel>.Instance);

        var dispatcher = new LangfuseTelemetryDispatcher(
            channel,
            api,
            monitor,
            TimeProvider.System,
            NullLogger<LangfuseTelemetryDispatcher>.Instance);

        dispatcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new RunningDispatcher(dispatcher);
    }

    private sealed class RunningDispatcher(LangfuseTelemetryDispatcher service) : IAsyncDisposable
    {
        public LangfuseTelemetryDispatcher Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            }
            catch (OperationCanceledException)
            {
                // Already stopped.
            }

            Service.Dispose();
        }
    }

    /// <summary>Captures dispatched batches, and can fail or partially reject them.</summary>
    private sealed class RecordingApi : ILangfuseApi
    {
        private readonly Lock _gate = new();
        private readonly List<IReadOnlyList<IngestionEventDto>> _batches = [];

        public Exception? Failure { get; set; }

        public IngestionResult? Response { get; set; }

        public IReadOnlyList<IReadOnlyList<IngestionEventDto>> Batches
        {
            get
            {
                lock (_gate)
                {
                    return [.. _batches];
                }
            }
        }

        public IReadOnlyList<IngestionEventDto> AllEvents => [.. Batches.SelectMany(b => b)];

        public Task<LangfusePrompt?> GetPromptAsync(string name, string? label, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IngestionResult> IngestAsync(
            IReadOnlyList<IngestionEventDto> events,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<IngestionResult>(Failure);
            }

            lock (_gate)
            {
                _batches.Add([.. events]);
            }

            return Task.FromResult(Response ?? new IngestionResult(events.Count, []));
        }
    }
}
