using System.Diagnostics;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Core.Tests.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Telemetry;

public sealed class TelemetryChannelTests
{
    [Fact]
    public void Tracking_a_generation_serialises_the_wire_body_immediately()
    {
        // Serialising on the caller's thread is what makes mutating the object afterwards safe.
        var channel = Build(out _);
        var generation = new LangfuseGeneration { Model = "claude-opus-4-8", Name = "chat" };

        channel.Track(generation).ShouldBeTrue();
        generation.Model = "mutated-after-track";

        channel.EventReader.TryRead(out var queued).ShouldBeTrue();
        queued!.Body!.ToJsonString().ShouldContain("claude-opus-4-8");
        queued.Body.ToJsonString().ShouldNotContain("mutated-after-track");
    }

    [Fact]
    public void Each_event_gets_the_right_wire_type()
    {
        var channel = Build(out _);

        channel.Track(new LangfuseTrace());
        channel.Track(new LangfuseGeneration());
        channel.Track(new LangfuseSpan());
        channel.Track(new LangfuseScore { Name = "quality" });

        var types = Drain(channel).Select(e => e.Type).ToArray();

        types.ShouldBe([
            IngestionEventTypes.TraceCreate,
            IngestionEventTypes.GenerationCreate,
            IngestionEventTypes.SpanCreate,
            IngestionEventTypes.ScoreCreate,
        ]);
    }

    [Fact]
    public void Every_event_carries_a_unique_id_so_a_retried_batch_cannot_double_count()
    {
        var channel = Build(out _);

        for (var i = 0; i < 20; i++)
        {
            channel.Track(new LangfuseGeneration());
        }

        Drain(channel).Select(e => e.Id).Distinct().Count().ShouldBe(20);
    }

    [Fact]
    public void Drops_rather_than_blocks_when_the_queue_is_full()
    {
        // The framework's core promise: recording telemetry never stalls application work, even
        // when the backend is gone and the queue has backed up.
        var channel = Build(out _, o => o.TelemetryQueueCapacity = 3);

        var accepted = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            if (channel.Track(new LangfuseGeneration()))
            {
                accepted++;
            }
        }

        stopwatch.Stop();

        accepted.ShouldBe(3);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Drop_oldest_keeps_the_most_recent_events()
    {
        // For debugging a live incident the newest events are the useful ones.
        var channel = Build(out _, o =>
        {
            o.TelemetryQueueCapacity = 2;
            o.OverflowPolicy = TelemetryOverflowPolicy.DropOldest;
        });

        channel.Track(new LangfuseGeneration { Name = "first" });
        channel.Track(new LangfuseGeneration { Name = "second" });
        channel.Track(new LangfuseGeneration { Name = "third" });

        var names = Drain(channel).Select(e => e.Body!["name"]!.GetValue<string>()).ToArray();

        names.ShouldBe(["second", "third"]);
    }

    [Fact]
    public void Tracking_is_a_no_op_when_telemetry_is_disabled()
    {
        var channel = Build(out _, o => o.EnableTelemetry = false);

        channel.Track(new LangfuseGeneration()).ShouldBeFalse();
        channel.EventReader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Applies_the_ambient_environment_and_release_to_every_event()
    {
        // Applied centrally so callers cannot forget, and so one project can separate deployments.
        var channel = Build(out _, o =>
        {
            o.Environment = "production";
            o.Release = "abc123";
        });

        channel.Track(new LangfuseTrace());
        channel.Track(new LangfuseGeneration());

        var events = Drain(channel);
        events[0].Body!["environment"]!.GetValue<string>().ShouldBe("production");
        events[0].Body!["release"]!.GetValue<string>().ShouldBe("abc123");
        events[1].Body!["environment"]!.GetValue<string>().ShouldBe("production");
    }

    [Fact]
    public void Omits_null_fields_so_an_update_never_clears_an_existing_value()
    {
        // Langfuse distinguishes an absent field from an explicit null on update events.
        var channel = Build(out _);

        channel.Track(new LangfuseGeneration { Model = "m" });

        var body = Drain(channel)[0].Body!.ToJsonString();
        body.ShouldNotContain("null");
    }

    [Fact]
    public void Maps_observation_levels_to_the_casing_langfuse_expects()
    {
        var channel = Build(out _);

        channel.Track(new LangfuseGeneration { Level = ObservationLevel.Error, StatusMessage = "boom" });

        Drain(channel)[0].Body!["level"]!.GetValue<string>().ShouldBe("ERROR");
    }

    [Fact]
    public void Stamps_events_from_the_injected_clock()
    {
        var channel = Build(out var time);
        time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        channel.Track(new LangfuseGeneration());

        Drain(channel)[0].Timestamp.ShouldBe(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Rejects_null_events()
    {
        var channel = Build(out _);

        Should.Throw<ArgumentNullException>(() => channel.Track((LangfuseTrace)null!));
        Should.Throw<ArgumentNullException>(() => channel.Track((LangfuseGeneration)null!));
        Should.Throw<ArgumentNullException>(() => channel.Track((LangfuseSpan)null!));
        Should.Throw<ArgumentNullException>(() => channel.Track((LangfuseScore)null!));
    }

    [Fact]
    public async Task Flush_returns_immediately_once_the_channel_is_closed()
    {
        // Shutdown must never deadlock a caller waiting on a flush that will never be serviced.
        var channel = Build(out _);
        channel.Complete();

        await channel.FlushAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    internal static LangfuseTelemetryChannel Build(
        out FakeTimeProvider time,
        Action<LangfuseOptions>? configure = null)
    {
        var options = new LangfuseOptions { PublicKey = "pk", SecretKey = "sk" };
        configure?.Invoke(options);

        time = new FakeTimeProvider();

        return new LangfuseTelemetryChannel(
            new StaticOptionsMonitor<LangfuseOptions>(options),
            time,
            NullLogger<LangfuseTelemetryChannel>.Instance);
    }

    private static List<IngestionEventDto> Drain(LangfuseTelemetryChannel channel)
    {
        var events = new List<IngestionEventDto>();
        while (channel.EventReader.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }
}
