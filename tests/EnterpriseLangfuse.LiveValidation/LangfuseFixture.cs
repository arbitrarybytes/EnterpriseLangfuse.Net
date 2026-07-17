using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnterpriseLangfuse;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EnterpriseLangfuse.LiveValidation;

/// <summary>
/// Shared connection to a real Langfuse, plus an <em>independent</em> HTTP client for verification.
/// </summary>
/// <remarks>
/// The independence matters. These tests exist to prove the hand-written wire contracts in
/// <c>EnterpriseLangfuse.Core</c> match what Langfuse actually expects. Verifying them by reading
/// back through the same contracts would be circular: a field this library names wrongly would be
/// written wrongly and read back wrongly, and the test would pass while production silently broke.
/// So every assertion reads the raw JSON with <see cref="JsonDocument"/> and checks the field names
/// Langfuse's own API documents.
/// </remarks>
public sealed class LangfuseFixture : IAsyncLifetime
{
    public bool IsConfigured { get; private set; }

    public string SkipReason =>
        "No Langfuse credentials. Set LANGFUSE_PUBLIC_KEY and LANGFUSE_SECRET_KEY (or create " +
        "langfuse.local.env at the repo root) to run live validation.";

    public IHost Host { get; private set; } = null!;

    public IPromptProvider Prompts => Host.Services.GetRequiredService<IPromptProvider>();

    public ILangfuseTelemetry Telemetry => Host.Services.GetRequiredService<ILangfuseTelemetry>();

    /// <summary>Raw client for verification reads. Never routed through this library's contracts.</summary>
    public HttpClient Verifier { get; private set; } = null!;

    /// <summary>Unique per run, so concurrent CI runs cannot collide on prompt names.</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("n")[..8];

    public ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddIniFile(FindRepoFile("langfuse.local.env"), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var publicKey = configuration["LANGFUSE_PUBLIC_KEY"];
        var secretKey = configuration["LANGFUSE_SECRET_KEY"];
        var baseUrl = new Uri(configuration["LANGFUSE_BASE_URL"] ?? "https://cloud.langfuse.com");

        IsConfigured = !string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(secretKey);
        if (!IsConfigured)
        {
            return ValueTask.CompletedTask;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddEnterpriseLangfuse(o =>
        {
            o.PublicKey = publicKey!;
            o.SecretKey = secretKey!;
            o.BaseUrl = baseUrl;
            o.Environment = "live-validation";
            o.Release = RunId;
            o.TelemetryFlushInterval = TimeSpan.FromMilliseconds(200);
            // Caching would mask a stale read while polling for a just-written prompt.
            o.PromptCacheDuration = TimeSpan.Zero;
            o.EnableOfflineFallback = false;   // a fallback here would hide a real API failure
        });

        Host = builder.Build();

        Verifier = new HttpClient { BaseAddress = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/") };
        Verifier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));

        return new ValueTask(Host.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        await Host.StopAsync();
        Host.Dispose();
        Verifier.Dispose();
    }

    /// <summary>
    /// Fetches a URL and returns the parsed JSON, asserting success.
    /// </summary>
    /// <remarks>
    /// Returns a cloned <see cref="JsonElement"/> rather than the <see cref="JsonDocument"/>: the
    /// element is only valid while its document is alive, so handing one out from a disposed document
    /// would be a use-after-free. Cloning detaches it from the document's pooled buffers.
    /// </remarks>
    public async Task<JsonElement> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await Verifier.GetAsync(uri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {uri} → {(int)response.StatusCode}: {body}", inner: null, response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Polls until <paramref name="probe"/> returns a value or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// Langfuse ingestion is asynchronous — an accepted event is queued, not immediately queryable —
    /// so a single read after writing would be flaky. Polling is the correct shape here.
    /// </remarks>
    public static async Task<T?> PollAsync<T>(
        Func<CancellationToken, Task<T?>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where T : struct
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await probe(cancellationToken) is { } result)
                {
                    return result;
                }
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.TooManyRequests)
            {
                // 404: not visible yet — ingestion is asynchronous. 429: Langfuse Cloud rate-limits
                // GET /traces/{id} to 15/min, so polling MUST treat it as retryable. Anything else
                // (401, 500) is a real failure and fails the test now, not after the full timeout.
            }

            // 5s, not 1s: three tests poll this endpoint concurrently against a 15/min budget, so a
            // tighter loop rate-limits itself into the timeout.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return null;
    }

    private static string FindRepoFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}

/// <summary>Shares one Langfuse connection across the whole live-validation suite.</summary>
[CollectionDefinition(Name)]
public sealed class LangfuseCollection : ICollectionFixture<LangfuseFixture>
{
    public const string Name = "langfuse-live";
}
