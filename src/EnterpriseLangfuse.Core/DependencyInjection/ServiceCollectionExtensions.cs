using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using EnterpriseLangfuse.Api;
using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Telemetry;
using Langfuse;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse;

/// <summary>Registers EnterpriseLangfuse in a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> shared by the prompt pipeline and the dispatcher.</summary>
    internal const string HttpClientName = "EnterpriseLangfuse";

    /// <summary>
    /// Adds the resilient prompt pipeline, background telemetry, and a configured AutoSDK client.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures credentials and behaviour.</param>
    /// <remarks>
    /// Registers, in order:
    /// <list type="bullet">
    /// <item>a resilient <see cref="HttpClient"/> (retry, circuit breaker, timeout);</item>
    /// <item><see cref="IPromptProvider"/> as L1 cache → L2 network → L3 embedded fallback;</item>
    /// <item><see cref="ILangfuseTelemetry"/> plus its background dispatcher;</item>
    /// <item><see cref="ILangfuseClient"/>, the AutoSDK client, for direct access to the rest of the API.</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddEnterpriseLangfuse(
        this IServiceCollection services,
        Action<LangfuseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<LangfuseOptions>()
            .Configure(configure)
            // Validate on start: bad credentials should fail the deployment, not the first request
            // that happens to need a prompt.
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<LangfuseOptions>, LangfuseOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddLogging();

        AddHttpPipeline(services);
        AddPromptPipeline(services);
        AddTelemetry(services);
        AddAutoSdkClient(services);

        return services;
    }

    /// <summary>
    /// Configures the transport with a standard resilience pipeline.
    /// </summary>
    /// <remarks>
    /// The retry/circuit-breaker/timeout strategies here are what turn a transient blip into a
    /// non-event; the L3 fallback only engages once resilience has genuinely given up.
    /// </remarks>
    private static void AddHttpPipeline(IServiceCollection services)
    {
        // RequestTimeout maps to the resilience pipeline's per-attempt timeout, not HttpClient.Timeout.
        // HttpClient.Timeout wraps the whole handler chain including every retry, so a 10s value there
        // means one slow attempt eats the entire budget and no retry can ever run. The names below are
        // the standard handler's documented option names ("{client}-standard"); the mapping is
        // asserted by a DI test so a rename upstream fails loudly instead of silently reverting to
        // defaults.
        services.AddOptions<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>($"{HttpClientName}-standard")
            .Configure<IOptions<LangfuseOptions>>((resilience, langfuse) =>
            {
                var attempt = langfuse.Value.RequestTimeout;

                resilience.AttemptTimeout.Timeout = attempt;
                // 3 retries + backoff must fit; 4x leaves room without hanging a caller indefinitely.
                resilience.TotalRequestTimeout.Timeout = attempt * 4;
                // The circuit breaker requires SamplingDuration >= 2x the attempt timeout.
                resilience.CircuitBreaker.SamplingDuration =
                    TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, attempt.Ticks * 2));
            });

        services.AddHttpClient(HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<LangfuseOptions>>().Value;

                client.BaseAddress = NormalizeBaseAddress(options.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.Authorization = BasicAuth(options);
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("EnterpriseLangfuse.NET", ThisAssemblyVersion));
            })
            // The factory's usual handler rotation cannot help here: LangfuseApi and the AutoSDK
            // client are singletons that capture their HttpClient once, pinning whatever handler was
            // current at startup. Bounding the connection lifetime on the handler itself restores
            // DNS re-resolution (failover, blue/green cutovers) for those pinned clients.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddStandardResilienceHandler();
    }

    private static void AddPromptPipeline(IServiceCollection services)
    {
        services.TryAddSingleton<ILangfuseApi>(provider => new LangfuseApi(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<ILogger<LangfuseApi>>()));

        services.TryAddSingleton<IEmbeddedPromptStore>(provider => new EmbeddedPromptStore(
            ResolveFallbackAssemblies(provider),
            provider.GetRequiredService<ILogger<EmbeddedPromptStore>>()));

        // Composed outermost-first: cache wraps fallback wraps network. Written as explicit
        // construction rather than DI decorators so the tier order is readable at a glance — it is
        // the whole design, and a mis-ordered pipeline would silently cache failures.
        services.TryAddSingleton<IPromptProvider>(provider =>
        {
            var network = new LangfusePromptProvider(provider.GetRequiredService<ILangfuseApi>());

            var withFallback = new FallbackPromptProvider(
                network,
                provider.GetRequiredService<IEmbeddedPromptStore>(),
                provider.GetRequiredService<IOptionsMonitor<LangfuseOptions>>(),
                provider.GetRequiredService<ILogger<FallbackPromptProvider>>());

            return new CachingPromptProvider(
                withFallback,
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IOptionsMonitor<LangfuseOptions>>());
        });
    }

    private static void AddTelemetry(IServiceCollection services)
    {
        services.TryAddSingleton<LangfuseTelemetryChannel>();
        services.TryAddSingleton<ILangfuseTelemetry>(p => p.GetRequiredService<LangfuseTelemetryChannel>());
        services.AddSingleton<IHostedService, LangfuseTelemetryDispatcher>();
    }

    /// <summary>
    /// Registers the AutoSDK client for the endpoints this framework does not wrap.
    /// </summary>
    /// <remarks>
    /// Shares the same resilient <see cref="HttpClient"/>, so AutoSDK calls inherit the retry and
    /// circuit-breaker policy. Note that the AutoSDK's own models cannot round-trip ingestion events
    /// or prompt reads (its <c>AllOf</c> converter drops the payload body), which is why this
    /// framework talks to those two endpoints directly — see LangfuseWireModels.cs.
    /// </remarks>
    private static void AddAutoSdkClient(IServiceCollection services)
    {
        services.TryAddSingleton<ILangfuseClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<LangfuseOptions>>().Value;
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            var client = new LangfuseClient(httpClient, baseUri: NormalizeBaseAddress(options.BaseUrl), disposeHttpClient: false);
            client.AuthorizeUsingBasic(options.PublicKey, options.SecretKey);
            return client;
        });
    }

    /// <summary>
    /// Assemblies to scan for embedded fallbacks: whatever was configured, else the entry assembly.
    /// </summary>
    private static IEnumerable<Assembly> ResolveFallbackAssemblies(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<LangfuseOptions>>().Value;

        if (options.FallbackAssemblies.Count > 0)
        {
            return options.FallbackAssemblies;
        }

        // Entry assembly is null under some test hosts; an empty store simply serves no fallbacks.
        var entry = Assembly.GetEntryAssembly();
        return entry is null ? [] : [entry];
    }

    /// <summary>
    /// Ensures the base address ends in a slash. Without it, <see cref="Uri"/> resolution silently
    /// drops the last path segment of a self-hosted URL such as <c>https://host/langfuse</c>.
    /// </summary>
    private static Uri NormalizeBaseAddress(Uri baseUrl) =>
        baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");

    private static AuthenticationHeaderValue BasicAuth(LangfuseOptions options)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{options.PublicKey}:{options.SecretKey}"));

        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private const string ThisAssemblyVersion = "0.1.0";
}
