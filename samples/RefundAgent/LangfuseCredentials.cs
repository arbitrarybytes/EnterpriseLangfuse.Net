using Microsoft.Extensions.Configuration;

namespace RefundAgent;

/// <summary>
/// Finds Langfuse credentials, or falls back to a deliberately unreachable host.
/// </summary>
/// <remarks>
/// The unreachable fallback is what lets this sample run with zero setup while still exercising the
/// real pipeline: the resilience tiers behave exactly as they would during an outage.
/// </remarks>
internal sealed record LangfuseCredentials(string PublicKey, string SecretKey, Uri BaseUrl, bool AreReal)
{
    /// <summary>
    /// A host guaranteed not to resolve. <c>.invalid</c> is reserved by RFC 2606 precisely so it can
    /// never be registered, which makes this a reliable stand-in for "Langfuse is down" — unlike
    /// localhost, which might have something listening.
    /// </summary>
    private static readonly Uri UnreachableHost = new("https://langfuse.invalid");

    public static LangfuseCredentials Discover()
    {
        // Order: env vars → langfuse.local.env → user secrets. Env vars win so CI can override.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<LangfuseCredentials>(optional: true)
            .AddIniFile(FindLocalEnvFile(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var publicKey = configuration["LANGFUSE_PUBLIC_KEY"];
        var secretKey = configuration["LANGFUSE_SECRET_KEY"];
        var baseUrl = configuration["LANGFUSE_BASE_URL"];

        var configured = !string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(secretKey);

        return new LangfuseCredentials(
            publicKey ?? "pk-lf-sample-not-configured",
            secretKey ?? "sk-lf-sample-not-configured",
            configured
                ? new Uri(baseUrl ?? "https://cloud.langfuse.com")
                : UnreachableHost,
            configured);
    }

    public void PrintBanner()
    {
        Console.WriteLine("EnterpriseLangfuse.NET — RefundAgent sample");
        Console.WriteLine(new string('═', 78));

        if (AreReal)
        {
            // Never print the secret; the public key prefix is enough to confirm which project.
            Console.WriteLine($"Langfuse : {BaseUrl}  (key {PublicKey[..Math.Min(11, PublicKey.Length)]}…)");
        }
        else
        {
            Console.WriteLine($"Langfuse : NOT CONFIGURED → pointing at {BaseUrl}");
            Console.WriteLine("           This is intentional: scenario 2 shows the app surviving an outage.");
            Console.WriteLine("           Set LANGFUSE_PUBLIC_KEY / LANGFUSE_SECRET_KEY to run against a real one.");
        }
    }

    /// <summary>Walks up to the repo root looking for a gitignored <c>langfuse.local.env</c>.</summary>
    private static string FindLocalEnvFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "langfuse.local.env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        // AddIniFile(optional: true) tolerates a path that does not exist.
        return Path.Combine(AppContext.BaseDirectory, "langfuse.local.env");
    }
}
