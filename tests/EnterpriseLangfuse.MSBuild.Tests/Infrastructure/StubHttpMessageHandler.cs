using System.Net;
using System.Text;

namespace EnterpriseLangfuse.MSBuild.Tests.Infrastructure;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> that records every request the task makes.
/// </summary>
/// <remarks>
/// Adapted from the Core tests' equivalent. Handwritten rather than mocked:
/// <see cref="HttpMessageHandler.SendAsync"/> is protected, so mocking frameworks reach it only
/// reflectively, and the recorded bodies here are what let a test assert the exact JSON the AutoSDK
/// put on the wire — the whole reason this path uses the AutoSDK at all.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();
    private int _callCount;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) =>
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

    /// <summary>Total requests seen.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Always responds 200 with an empty JSON object, which the create endpoint accepts.</summary>
    public static StubHttpMessageHandler AlwaysSucceeds() =>
        new((_, _) => Json(HttpStatusCode.OK, "{}"));

    /// <summary>Always responds with the given status and body.</summary>
    public static StubHttpMessageHandler AlwaysFails(HttpStatusCode status, string body = "{\"message\":\"denied\"}") =>
        new((_, _) => Json(status, body));

    /// <summary>Throws, simulating DNS failure or a connection reset.</summary>
    public static StubHttpMessageHandler AlwaysThrows(Exception exception) =>
        new((_, _) => throw exception);

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
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                body,
                request.Headers.Authorization?.ToString()));
        }

        return _responder(request, callIndex);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body, string? Authorization);
