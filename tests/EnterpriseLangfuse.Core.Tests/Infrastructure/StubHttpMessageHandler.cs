using System.Net;
using System.Text;

namespace EnterpriseLangfuse.Core.Tests.Infrastructure;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> for driving the pipeline through failure modes that
/// are otherwise impossible to reproduce — 502s, timeouts, malformed bodies.
/// </summary>
/// <remarks>
/// Handwritten rather than mocked: <see cref="HttpMessageHandler.SendAsync"/> is protected, so
/// mocking frameworks reach it only reflectively, and the recorded-request list here makes
/// assertions about batching and retries far more direct.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _responder;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();
    private int _callCount;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    /// <summary>Every request seen, with its body captured before disposal.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Total requests seen, including retries.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Always responds with the given status.</summary>
    public static StubHttpMessageHandler AlwaysFails(HttpStatusCode status = HttpStatusCode.BadGateway) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain"),
        }));

    /// <summary>Always responds 200 with the given JSON body.</summary>
    public static StubHttpMessageHandler AlwaysReturnsJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    /// <summary>Throws, simulating DNS failure or a connection reset.</summary>
    public static StubHttpMessageHandler AlwaysThrows(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var callIndex = Interlocked.Increment(ref _callCount) - 1;

        // The body must be read now: HttpClient disposes the request content once the call returns,
        // so deferring this to the assertion would read a disposed stream.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, request.Headers.Authorization?.ToString()));
        }

        return await _responder(request, callIndex);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body, string? Authorization);
