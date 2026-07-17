using System.Runtime.CompilerServices;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI.Tests.Infrastructure;

/// <summary>Captures what the chat client tracked, so tests can assert on it.</summary>
internal sealed class RecordingTelemetry : ILangfuseTelemetry
{
    public List<LangfuseTrace> Traces { get; } = [];

    public List<LangfuseGeneration> Generations { get; } = [];

    public List<LangfuseSpan> Spans { get; } = [];

    public List<LangfuseScore> Scores { get; } = [];

    public bool Track(LangfuseTrace trace)
    {
        Traces.Add(trace);
        return true;
    }

    public bool Track(LangfuseGeneration generation)
    {
        Generations.Add(generation);
        return true;
    }

    public bool Track(LangfuseSpan span)
    {
        Spans.Add(span);
        return true;
    }

    public bool Track(LangfuseScore score)
    {
        Scores.Add(score);
        return true;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>An <see cref="IChatClient"/> that returns a scripted response or throws.</summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<Task<ChatResponse>>? _responder;
    private readonly Func<IEnumerable<ChatResponseUpdate>>? _streamer;

    public FakeChatClient(ChatResponse response) => _responder = () => Task.FromResult(response);

    public FakeChatClient(Exception failure) => _responder = () => Task.FromException<ChatResponse>(failure);

    public FakeChatClient(Func<IEnumerable<ChatResponseUpdate>> streamer) => _streamer = streamer;

    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = [.. messages];
        return _responder!();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = [.. messages];

        foreach (var update in _streamer!())
        {
            // Yield to the scheduler so the stream behaves asynchronously, as a real provider would.
            await Task.Yield();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
