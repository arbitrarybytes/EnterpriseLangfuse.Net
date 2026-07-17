using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse.Telemetry;

/// <summary>
/// The non-blocking telemetry queue: <see cref="ILangfuseTelemetry"/> writes here,
/// <see cref="LangfuseTelemetryDispatcher"/> drains it.
/// </summary>
/// <remarks>
/// The event channel is <b>bounded</b>. An unbounded queue would turn a Langfuse outage into
/// unbounded memory growth in the host application — a worse failure than the one it avoids. Bounded
/// plus a drop policy means a telemetry outage costs telemetry, never the process.
/// <para>
/// Events are serialised to their wire form at <em>enqueue</em> time, on the caller's thread. That is
/// deliberate: the caller may still mutate the object it passed in, so reading it later on the
/// dispatcher thread would be a data race; and it keeps serialisation cost off the single dispatcher,
/// which would otherwise be the bottleneck under load.
/// </para>
/// </remarks>
internal sealed class LangfuseTelemetryChannel : ILangfuseTelemetry
{
    private readonly Channel<IngestionEventDto> _events;

    /// <summary>
    /// Flush requests travel on their own unbounded channel rather than as a marker in the event
    /// queue: a marker would be silently discarded by the event queue's drop policy exactly when the
    /// queue is full, hanging <see cref="FlushAsync"/> at the worst possible moment.
    /// </summary>
    private readonly Channel<TaskCompletionSource> _flushRequests =
        Channel.CreateUnbounded<TaskCompletionSource>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IOptionsMonitor<LangfuseOptions> _options;
    private readonly ILogger<LangfuseTelemetryChannel> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly bool _dropOldest;

    public LangfuseTelemetryChannel(
        IOptionsMonitor<LangfuseOptions> options,
        TimeProvider timeProvider,
        ILogger<LangfuseTelemetryChannel> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;

        var current = options.CurrentValue;
        _capacity = current.TelemetryQueueCapacity;

        _dropOldest = current.OverflowPolicy == TelemetryOverflowPolicy.DropOldest;

        _events = Channel.CreateBounded<IngestionEventDto>(
            new BoundedChannelOptions(_capacity)
            {
                // DropNewest maps to Wait, not the obvious DropWrite. Under DropWrite, TryWrite
                // discards the event and still returns *true*, so a drop is indistinguishable from a
                // success: Track would report acceptance and the dropped-event metric could never
                // fire, hiding telemetry loss exactly when it matters. Under Wait, TryWrite returns
                // false when the queue is full without blocking, which is the same drop-the-newest
                // behaviour but observable. It never blocks because nothing here calls WriteAsync.
                FullMode = _dropOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,

                // A single dispatcher drains the queue, which lets it batch without extra locking.
                SingleReader = true,
                SingleWriter = false,
            });
    }

    internal ChannelReader<IngestionEventDto> EventReader => _events.Reader;

    internal ChannelReader<TaskCompletionSource> FlushReader => _flushRequests.Reader;

    public bool Track(LangfuseTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        // Checked before mapping/serialisation, not just in Enqueue: the disable switch must cost
        // nothing on the hot path, not "full wire serialisation, then a drop".
        if (!_options.CurrentValue.EnableTelemetry)
        {
            return false;
        }

        return Enqueue(
            IngestionEventTypes.TraceCreate,
            JsonSerializer.SerializeToNode(
                TelemetryMapper.ToBody(trace, _options.CurrentValue),
                LangfuseJsonContext.Default.TraceBodyDto));
    }

    public bool Track(LangfuseGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);

        // Checked before mapping/serialisation, not just in Enqueue: the disable switch must cost
        // nothing on the hot path, not "full wire serialisation, then a drop".
        if (!_options.CurrentValue.EnableTelemetry)
        {
            return false;
        }

        return Enqueue(
            IngestionEventTypes.GenerationCreate,
            JsonSerializer.SerializeToNode(
                TelemetryMapper.ToBody(generation, _options.CurrentValue),
                LangfuseJsonContext.Default.GenerationBodyDto));
    }

    public bool Track(LangfuseSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        // Checked before mapping/serialisation, not just in Enqueue: the disable switch must cost
        // nothing on the hot path, not "full wire serialisation, then a drop".
        if (!_options.CurrentValue.EnableTelemetry)
        {
            return false;
        }

        return Enqueue(
            IngestionEventTypes.SpanCreate,
            JsonSerializer.SerializeToNode(
                TelemetryMapper.ToBody(span, _options.CurrentValue),
                LangfuseJsonContext.Default.SpanBodyDto));
    }

    public bool Track(LangfuseScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        // Checked before mapping/serialisation, not just in Enqueue: the disable switch must cost
        // nothing on the hot path, not "full wire serialisation, then a drop".
        if (!_options.CurrentValue.EnableTelemetry)
        {
            return false;
        }

        return Enqueue(
            IngestionEventTypes.ScoreCreate,
            JsonSerializer.SerializeToNode(
                TelemetryMapper.ToBody(score, _options.CurrentValue),
                LangfuseJsonContext.Default.ScoreBodyDto));
    }

    private bool Enqueue(string eventType, JsonNode? body)
    {
        if (!_options.CurrentValue.EnableTelemetry)
        {
            return false;
        }

        var dto = new IngestionEventDto
        {
            // Langfuse deduplicates on this id, so a retried batch cannot double-count an event.
            Id = TraceIdentifier.New(),
            Type = eventType,
            Timestamp = _timeProvider.GetUtcNow(),
            Body = body,
        };

        // Under DropOldest the channel evicts silently and TryWrite still returns true, so a full
        // queue has to be detected before the write to keep the drop observable. This read is racy by
        // nature, which is acceptable for a metric but is why it is not used for the return value.
        var evicting = _dropOldest && _events.Reader.Count >= _capacity;

        // TryWrite never blocks in either mode; see the FullMode note in the constructor.
        if (_events.Writer.TryWrite(dto))
        {
            if (evicting)
            {
                RecordDrop(eventType);
            }

            LangfuseMetrics.TelemetryEnqueued.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
            return true;
        }

        RecordDrop(eventType);
        return false;
    }

    private void RecordDrop(string eventType)
    {
        LangfuseMetrics.TelemetryDropped.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
        _logger.TelemetryEventDropped(_capacity, eventType);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_flushRequests.Writer.TryWrite(request))
        {
            // The dispatcher has stopped and completed the channel; there is nothing left to wait for.
            return;
        }

        await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes both queues so the dispatcher drains and stops.</summary>
    internal void Complete()
    {
        _events.Writer.TryComplete();
        _flushRequests.Writer.TryComplete();
    }
}
