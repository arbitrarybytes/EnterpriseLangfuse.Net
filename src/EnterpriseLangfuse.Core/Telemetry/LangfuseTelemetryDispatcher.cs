using System.Diagnostics;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse.Telemetry;

/// <summary>
/// Drains <see cref="LangfuseTelemetryChannel"/> and dispatches batches to Langfuse.
/// </summary>
/// <remarks>
/// Runs as a single background consumer, so batching needs no locking. Its guiding rule is that
/// nothing here may ever surface to the application: a failed batch is logged and dropped, never
/// rethrown, because an unhandled exception in a <see cref="BackgroundService"/> tears down the host
/// by default — telemetry must not be able to kill the process it observes.
/// </remarks>
internal sealed class LangfuseTelemetryDispatcher : BackgroundService
{
    private readonly LangfuseTelemetryChannel _channel;
    private readonly ILangfuseApi _api;
    private readonly IOptionsMonitor<LangfuseOptions> _options;
    private readonly ILogger<LangfuseTelemetryDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    public LangfuseTelemetryDispatcher(
        LangfuseTelemetryChannel channel,
        ILangfuseApi api,
        IOptionsMonitor<LangfuseOptions> options,
        TimeProvider timeProvider,
        ILogger<LangfuseTelemetryDispatcher> logger)
    {
        _channel = channel;
        _api = api;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var batch = new List<IngestionEventDto>(_options.CurrentValue.TelemetryBatchSize);
        var flushing = new List<TaskCompletionSource>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var work = await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            if (work == WorkKind.Stopped)
            {
                break;
            }

            if (work == WorkKind.Flush)
            {
                // Snapshot the requests BEFORE draining, and complete only that snapshot after.
                // Completing whatever is queued after the drain would release a caller whose
                // events arrived mid-drain and are still sitting in the queue — a FlushAsync that
                // returns before its own events were sent.
                flushing.Clear();
                while (_channel.FlushReader.TryRead(out var request))
                {
                    flushing.Add(request);
                }
            }

            await DrainAsync(batch, flushEverything: work == WorkKind.Flush, stoppingToken).ConfigureAwait(false);

            foreach (var request in flushing)
            {
                request.TrySetResult();
            }

            flushing.Clear();
        }
    }

    private enum WorkKind
    {
        Events,
        Flush,
        Stopped,
    }

    /// <summary>Pending waiters, kept across iterations. See <see cref="WaitForWorkAsync"/>.</summary>
    private Task<bool>? _eventsAvailable;
    private Task<bool>? _flushRequested;

    /// <summary>
    /// Blocks until events arrive, a flush is requested, or shutdown.
    /// </summary>
    /// <remarks>
    /// The two waiters persist across loop iterations rather than being recreated each time. With
    /// fresh tasks per iteration, the loser of every <see cref="Task.WhenAny(Task[])"/> is abandoned
    /// while still registered with its channel, so waiters accumulate until data arrives — and each
    /// consumed no-op wakeups on later iterations. Reusing the incomplete task keeps exactly one
    /// waiter per channel alive.
    /// </remarks>
    private async Task<WorkKind> WaitForWorkAsync(CancellationToken stoppingToken)
    {
        _eventsAvailable ??= _channel.EventReader.WaitToReadAsync(stoppingToken).AsTask();
        _flushRequested ??= _channel.FlushReader.WaitToReadAsync(stoppingToken).AsTask();

        await Task.WhenAny(_eventsAvailable, _flushRequested).ConfigureAwait(false);

        // Flush wins ties: it drains everything anyway, and answering it first keeps FlushAsync
        // callers from waiting out an ordinary batch cycle.
        if (_flushRequested.IsCompleted)
        {
            var open = await _flushRequested.ConfigureAwait(false);
            _flushRequested = null;
            return open ? WorkKind.Flush : WorkKind.Stopped;
        }

        var hasEvents = await _eventsAvailable.ConfigureAwait(false);
        _eventsAvailable = null;
        return hasEvents ? WorkKind.Events : WorkKind.Stopped;
    }

    /// <summary>
    /// Fills and sends batches from whatever is queued.
    /// </summary>
    /// <param name="batch">Reused batch buffer, cleared after each send.</param>
    /// <param name="flushEverything">
    /// When true, keep going until the queue is empty (a flush must not leave a partial batch behind).
    /// When false, wait up to the flush interval for a full batch, then send what accumulated.
    /// </param>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    private async Task DrainAsync(List<IngestionEventDto> batch, bool flushEverything, CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        var batchSize = options.TelemetryBatchSize;

        if (flushEverything)
        {
            // Send everything queued right now, in batch-sized chunks, and leave nothing behind.
            while (_channel.EventReader.TryRead(out var item))
            {
                batch.Add(item);
                if (batch.Count >= batchSize)
                {
                    await SendAndClearAsync(batch, stoppingToken).ConfigureAwait(false);
                }
            }

            await SendAndClearAsync(batch, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Accumulate until the batch is full or the flush interval elapses, so that a trickle of
        // events costs one request per interval rather than one request per event.
        var deadline = _timeProvider.GetUtcNow() + options.TelemetryFlushInterval;

        while (batch.Count < batchSize)
        {
            while (batch.Count < batchSize && _channel.EventReader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count >= batchSize)
            {
                break;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                using var timeout = new CancellationTokenSource(remaining, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

                var moreEvents = _channel.EventReader.WaitToReadAsync(linked.Token).AsTask();

                // A flush arriving mid-accumulation must cut the batch short. Without this, the
                // Track-then-FlushAsync pattern (every CLI and test) waits out the remainder of the
                // flush interval before the flush request is even looked at. _flushRequested is the
                // persistent waiter owned by WaitForWorkAsync; completing here leaves it for the
                // outer loop to consume, which then runs the actual flush drain.
                if (_flushRequested is { } flushArrived)
                {
                    var completed = await Task.WhenAny(moreEvents, flushArrived).ConfigureAwait(false);
                    if (completed == flushArrived)
                    {
                        // Wake the abandoned event waiter so it settles instead of lingering.
                        await linked.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }

                // False means the channel completed: no more events will ever arrive.
                if (!await moreEvents.ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // The interval elapsed, or we are shutting down. Either way, send what we have.
                break;
            }
        }

        await SendAndClearAsync(batch, stoppingToken).ConfigureAwait(false);
    }

    private async Task SendAndClearAsync(List<IngestionEventDto> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await SendAsync(batch, cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private async Task SendAsync(List<IngestionEventDto> batch, CancellationToken cancellationToken)
    {
        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            var result = await _api.IngestAsync(batch, cancellationToken).ConfigureAwait(false);

            LangfuseMetrics.TelemetryDispatched.Add(result.SuccessCount);

            // Per-event rejections must land in the dropped counter, or enqueued never reconciles
            // with dispatched + dropped and rejected events vanish from the metrics entirely.
            if (result.Errors.Count > 0)
            {
                LangfuseMetrics.TelemetryDropped.Add(result.Errors.Count);
            }

            _logger.TelemetryBatchFlushed(result.SuccessCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Never rethrow: this runs on the host's background task, and faulting it would stop the
            // application. Telemetry is best-effort by design.
            LangfuseMetrics.TelemetryDropped.Add(batch.Count);
            _logger.IngestionBatchFailed(batch.Count, ex);
        }
        finally
        {
            LangfuseMetrics.IngestionDuration.Record(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
        }
    }

    private void CompletePendingFlushes()
    {
        while (_channel.FlushReader.TryRead(out var request))
        {
            request.TrySetResult();
        }
    }

    /// <summary>
    /// Drains what is queued at shutdown, bounded by <see cref="LangfuseOptions.ShutdownDrainTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Without a drain, every trace still in the queue is lost on each deployment. Without the
    /// timeout, an unreachable Langfuse would hang shutdown — so the drain is best-effort and
    /// time-boxed, and any shortfall is logged rather than waited out.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting work and let ExecuteAsync observe the completed channels.
        _channel.Complete();

        using var drainTimeout = new CancellationTokenSource(_options.CurrentValue.ShutdownDrainTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, drainTimeout.Token);

        // Wait for ExecuteAsync to exit before this thread reads from the channels. It observes the
        // completed channels above and stops on its own; draining concurrently with it would mean
        // two readers on channels created SingleReader — undefined by the channel contract, even if
        // today's BoundedChannel happens to tolerate it. The wait is graceful (no cancellation), so
        // an in-flight send finishes rather than being cut off mid-request.
        if (ExecuteTask is { } executing)
        {
            try
            {
                await executing.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Drain-timeout elapsed while a send was still in flight; fall through and let
                // base.StopAsync cancel it. Skip the drain — ExecuteAsync may still be reading.
                _logger.ShutdownDrainTimedOut(_channel.EventReader.Count);
                CompletePendingFlushes();
                await base.StopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            await DrainRemainingAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.ShutdownDrainTimedOut(_channel.EventReader.Count);
        }

        // Unblock anyone awaiting FlushAsync so shutdown cannot deadlock a caller.
        CompletePendingFlushes();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainRemainingAsync(CancellationToken cancellationToken)
    {
        var batchSize = _options.CurrentValue.TelemetryBatchSize;
        var batch = new List<IngestionEventDto>(batchSize);

        while (_channel.EventReader.TryRead(out var item))
        {
            batch.Add(item);

            if (batch.Count >= batchSize)
            {
                await SendAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (batch.Count > 0)
        {
            await SendAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }
}
