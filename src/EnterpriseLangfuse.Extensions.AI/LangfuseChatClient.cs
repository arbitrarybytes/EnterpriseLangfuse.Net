using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using EnterpriseLangfuse.Diagnostics;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that traces every LLM call to Langfuse.
/// </summary>
/// <remarks>
/// Wraps any <see cref="IChatClient"/> and records model, tokens, tools, timings and (optionally)
/// content, then hands the result to <see cref="ILangfuseTelemetry"/> — an in-memory queue drained by
/// a background service. No Langfuse call happens on the caller's path, so tracing adds no network
/// latency to an LLM request and a Langfuse outage cannot fail one.
/// <para>
/// Spans are real <see cref="Activity"/> instances on a W3C-propagating
/// <see cref="ActivitySource"/>, and the Langfuse trace id is taken from the ambient
/// <see cref="Activity.TraceId"/>. That means a Langfuse trace and the surrounding OpenTelemetry
/// trace share an id, so a generation can be pivoted back to the HTTP request that caused it.
/// </para>
/// </remarks>
public sealed class LangfuseChatClient : DelegatingChatClient
{
    private readonly ILangfuseTelemetry _telemetry;
    private readonly LangfuseChatClientOptions _options;

    /// <param name="innerClient">The client actually performing the LLM call.</param>
    /// <param name="telemetry">The non-blocking telemetry queue.</param>
    /// <param name="options">Capture behaviour; defaults are used when null.</param>
    public LangfuseChatClient(
        IChatClient innerClient,
        ILangfuseTelemetry telemetry,
        LangfuseChatClientOptions? options = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        _telemetry = telemetry;
        _options = options ?? new LangfuseChatClientOptions();
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Materialise once: the caller may hand us a lazy enumerable, and enumerating it both for
        // tracing and for the inner client would execute it twice.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        var parent = Activity.Current;
        using var activity = LangfuseMetrics.ActivitySource.StartActivity("chat", ActivityKind.Client);

        var generation = StartGeneration(messageList, options, activity, parent);
        EnsureTrace(generation, messageList, options, isRoot: parent is null);

        // The prompt entry must not travel to the inner client; see StripForDelegation.
        var forwarded = LangfusePromptContext.StripForDelegation(options);

        try
        {
            var response = await base.GetResponseAsync(messageList, forwarded, cancellationToken).ConfigureAwait(false);

            Complete(generation, response, activity);
            return response;
        }
        catch (Exception ex)
        {
            Fail(generation, ex, activity);
            throw;
        }
        finally
        {
            // Tracked in `finally` so a failed call is still recorded. An LLM call that threw is
            // precisely the one worth seeing in Langfuse.
            generation.EndTime = DateTimeOffset.UtcNow;
            _telemetry.Track(generation);
        }
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        var parent = Activity.Current;
        using var activity = LangfuseMetrics.ActivitySource.StartActivity("chat", ActivityKind.Client);

        var generation = StartGeneration(messageList, options, activity, parent);
        EnsureTrace(generation, messageList, options, isRoot: parent is null);

        var forwarded = LangfusePromptContext.StripForDelegation(options);

        var text = new StringBuilder();
        var updates = new List<ChatResponseUpdate>();

        // try/catch cannot wrap a `yield return`, so failures are captured around each MoveNext
        // instead and re-thrown after the generation has been recorded.
        var enumerator = base.GetStreamingResponseAsync(messageList, forwarded, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex)
                {
                    Fail(generation, ex, activity);
                    throw;
                }

                // Time-to-first-token: the metric that actually reflects perceived latency for a
                // streamed response, and the reason streaming is instrumented separately at all.
                generation.CompletionStartTime ??= DateTimeOffset.UtcNow;

                // With content capture off, only updates carrying signal beyond text (usage, tool
                // calls, model id, finish reason) are retained — buffering every chunk of a long
                // agent stream would hold the entire response in memory for nothing.
                if (_options.CaptureContent)
                {
                    updates.Add(update);
                    if (update.Text is { Length: > 0 } chunk)
                    {
                        text.Append(chunk);
                    }
                }
                else if (CarriesNonTextSignal(update))
                {
                    updates.Add(update);
                }

                yield return update;
            }

            CompleteStreaming(generation, updates, text, activity);
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);

            generation.EndTime = DateTimeOffset.UtcNow;
            _telemetry.Track(generation);
        }
    }

    private LangfuseGeneration StartGeneration(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        Activity? activity,
        Activity? parent)
    {
        var generation = new LangfuseGeneration
        {
            // Reuse the W3C span id so the Langfuse observation and the OTel span are the same thing
            // under two names. Falls back to a fresh id when no listener is sampling the source.
            Id = activity?.SpanId.ToHexString() ?? TraceIdentifier.New(),
            TraceId = ResolveTraceId(activity),
            // Only a parent span created by THIS library corresponds to a Langfuse observation. An
            // ambient ASP.NET Core or HttpClient span has a perfectly valid span id that references
            // an observation that will never exist, which orphans the generation in the Langfuse UI.
            ParentObservationId = parent?.Source == LangfuseMetrics.ActivitySource
                ? parent.SpanId.ToHexString()
                : null,
            Name = _options.OperationName ?? options?.ModelId ?? "chat",
            StartTime = DateTimeOffset.UtcNow,
            Model = options?.ModelId,
            ModelParameters = BuildModelParameters(options),
            Input = _options.CaptureContent ? ChatPayloadSerializer.SerializeMessages(messages) : null,
            Metadata = BuildMetadata(options),
        };

        // Link the generation to the prompt revision that produced it, when the caller used a
        // prompt from this framework. This is what makes prompt-level evaluation possible.
        if (options is not null && LangfusePromptContext.TryGet(options, out var prompt))
        {
            generation.PromptName = prompt.Name;
            generation.PromptVersion = prompt.Version;
        }

        return generation;
    }

    /// <summary>
    /// Emits a trace when this call is the root of the operation.
    /// </summary>
    /// <remarks>
    /// Langfuse implicitly creates a trace for an unknown trace id, but that implicit trace has no
    /// name, user or session. Emitting one explicitly at the root is what makes the Langfuse UI
    /// usable; doing it for nested calls would overwrite the parent's trace metadata.
    /// </remarks>
    private void EnsureTrace(
        LangfuseGeneration generation,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? chatOptions,
        bool isRoot)
    {
        if (!isRoot || generation.TraceId is null)
        {
            return;
        }

        _telemetry.Track(new LangfuseTrace
        {
            Id = generation.TraceId,
            Name = generation.Name,
            Timestamp = generation.StartTime,
            UserId = _options.UserIdAccessor?.Invoke(),
            // ConversationId is MEAI's own thread identifier, so it is the natural default session.
            SessionId = _options.SessionIdAccessor?.Invoke() ?? chatOptions?.ConversationId,
            Input = _options.CaptureContent ? ChatPayloadSerializer.SerializeMessages(messages) : null,
            Tags = _options.Tags.Count > 0 ? [.. _options.Tags] : null,
        });
    }

    private void Complete(LangfuseGeneration generation, ChatResponse response, Activity? activity)
    {
        // The response knows the model that actually served the request, which can differ from the
        // requested one (aliases, fallbacks). Prefer it — cost attribution depends on it.
        generation.Model = response.ModelId ?? generation.Model;
        generation.Usage = ChatPayloadSerializer.ToUsage(response.Usage);
        generation.Output = _options.CaptureContent ? ChatPayloadSerializer.SerializeResponse(response) : null;

        activity?.SetTag("gen_ai.response.model", generation.Model);
        activity?.SetTag("gen_ai.response.finish_reason", response.FinishReason?.Value);
        SetUsageTags(activity, response.Usage);
    }

    private void CompleteStreaming(
        LangfuseGeneration generation,
        List<ChatResponseUpdate> updates,
        StringBuilder text,
        Activity? activity)
    {
        // Reconstructing the response gives the same usage/model/finish-reason shape as the
        // non-streaming path, so both produce identical-looking traces in Langfuse.
        var response = updates.ToChatResponse();

        generation.Model = response.ModelId ?? generation.Model;
        generation.Usage = ChatPayloadSerializer.ToUsage(response.Usage);
        generation.Output = _options.CaptureContent
            ? ChatPayloadSerializer.SerializeStreamedOutput(response, text.ToString())
            : null;

        activity?.SetTag("gen_ai.response.model", generation.Model);
        activity?.SetTag("gen_ai.response.finish_reason", response.FinishReason?.Value);
        SetUsageTags(activity, response.Usage);
    }

    /// <summary>True when an update matters to the trace even with content capture disabled.</summary>
    private static bool CarriesNonTextSignal(ChatResponseUpdate update)
    {
        if (update.FinishReason is not null || update.ModelId is not null)
        {
            return true;
        }

        foreach (var content in update.Contents)
        {
            if (content is not TextContent)
            {
                return true;
            }
        }

        return false;
    }

    private static void Fail(LangfuseGeneration generation, Exception exception, Activity? activity)
    {
        generation.Level = ObservationLevel.Error;
        generation.StatusMessage = exception.Message;

        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddTag("error.type", exception.GetType().FullName);
    }

    /// <summary>
    /// Uses the ambient W3C trace id so Langfuse and OpenTelemetry agree on what a trace is.
    /// </summary>
    private static string? ResolveTraceId(Activity? activity)
    {
        var traceId = activity?.TraceId ?? Activity.Current?.TraceId;

        // A default TraceId means nothing is sampling this source; fall back to a generated id so
        // the generation still lands under some trace rather than being orphaned.
        return traceId is { } id && id != default ? id.ToHexString() : TraceIdentifier.New();
    }

    private static JsonNode? BuildModelParameters(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var node = new JsonObject();
        AddIfSet(node, "temperature", options.Temperature);
        AddIfSet(node, "max_tokens", options.MaxOutputTokens);
        AddIfSet(node, "top_p", options.TopP);
        AddIfSet(node, "top_k", options.TopK);
        AddIfSet(node, "frequency_penalty", options.FrequencyPenalty);
        AddIfSet(node, "presence_penalty", options.PresencePenalty);
        AddIfSet(node, "seed", options.Seed);

        if (options.StopSequences is { Count: > 0 } stops)
        {
            node["stop"] = new JsonArray([.. stops.Select(s => (JsonNode)JsonValue.Create(s))]);
        }

        return node.Count > 0 ? node : null;
    }

    /// <summary>
    /// Records which tools were offered to the model.
    /// </summary>
    /// <remarks>
    /// Only names are captured, not schemas: schemas are large, static, and identical on every call,
    /// so sending them would inflate every trace for no diagnostic gain.
    /// </remarks>
    private static JsonNode? BuildMetadata(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return null;
        }

        return new JsonObject
        {
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)JsonValue.Create(t.Name))]),
            ["tool_mode"] = JsonValue.Create(options.ToolMode?.ToString() ?? "auto"),
        };
    }

    // One overload per concrete type rather than a single generic AddIfSet<T>. A type parameter binds
    // to JsonValue.Create<T>, which is annotated RequiresDynamicCode/RequiresUnreferencedCode because
    // it can fall back to reflection-based serialisation — that would break Native AOT. The
    // non-generic Create overloads carry no such annotation, and only a concrete argument type
    // selects them.
    private static void AddIfSet(JsonObject node, string name, float? value)
    {
        if (value.HasValue)
        {
            node[name] = JsonValue.Create(value.Value);
        }
    }

    private static void AddIfSet(JsonObject node, string name, int? value)
    {
        if (value.HasValue)
        {
            node[name] = JsonValue.Create(value.Value);
        }
    }

    private static void AddIfSet(JsonObject node, string name, long? value)
    {
        if (value.HasValue)
        {
            node[name] = JsonValue.Create(value.Value);
        }
    }

    private static void SetUsageTags(Activity? activity, UsageDetails? usage)
    {
        if (activity is null || usage is null)
        {
            return;
        }

        activity.SetTag("gen_ai.usage.input_tokens", usage.InputTokenCount);
        activity.SetTag("gen_ai.usage.output_tokens", usage.OutputTokenCount);
    }
}
