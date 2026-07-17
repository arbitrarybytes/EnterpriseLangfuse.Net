using System.Collections.Concurrent;
using System.Reflection;
using EnterpriseLangfuse.Shared;
using Microsoft.Extensions.Logging;

namespace EnterpriseLangfuse.Prompts;

/// <summary>Supplies prompts compiled into the application as embedded resources.</summary>
internal interface IEmbeddedPromptStore
{
    /// <summary>Returns the embedded prompt with this name, or null if none was compiled in.</summary>
    LangfusePrompt? TryGet(string name);

    /// <summary>Names of every embedded prompt available. Used by diagnostics and tests.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>
/// Loads <c>*.prompt.yaml</c> embedded resources, providing the L3 offline tier.
/// </summary>
/// <remarks>
/// Resources are enumerated and parsed once, lazily, on first use — not in the constructor. Parsing
/// at construction would move file-format errors into DI container build, failing application startup
/// for a fallback that might never be needed.
/// <para>
/// A malformed resource is logged and skipped rather than thrown: one bad prompt file must not
/// deny the fallback tier to every other prompt, which is the exact moment this tier matters.
/// </para>
/// </remarks>
internal sealed class EmbeddedPromptStore : IEmbeddedPromptStore
{
    /// <summary>Resource-name suffix identifying a prompt file.</summary>
    internal const string ResourceSuffix = ".prompt.yaml";

    private readonly Lazy<IReadOnlyDictionary<string, LangfusePrompt>> _prompts;

    public EmbeddedPromptStore(IEnumerable<Assembly> assemblies, ILogger<EmbeddedPromptStore> logger)
    {
        // Materialise the assembly list eagerly, but defer the actual resource read.
        var sources = assemblies.Distinct().ToArray();
        _prompts = new Lazy<IReadOnlyDictionary<string, LangfusePrompt>>(
            () => Load(sources, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyCollection<string> Names => _prompts.Value.Keys.ToArray();

    public LangfusePrompt? TryGet(string name) => _prompts.Value.GetValueOrDefault(name);

    private static IReadOnlyDictionary<string, LangfusePrompt> Load(Assembly[] assemblies, ILogger logger)
    {
        var prompts = new ConcurrentDictionary<string, LangfusePrompt>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            var loaded = 0;

            foreach (var resourceName in GetPromptResourceNames(assembly))
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream is null)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(stream);
                    var document = PromptYamlParser.Parse(reader, DeriveName(resourceName));
                    var prompt = LangfusePrompt.FromDocument(document, PromptSource.EmbeddedFallback);

                    // First assembly wins, so an application can shadow a library's prompt by
                    // registering its own assembly first.
                    if (prompts.TryAdd(prompt.Name, prompt))
                    {
                        loaded++;
                    }
                }
                catch (Exception ex) when (ex is PromptYamlException or IOException)
                {
                    logger.EmbeddedFallbackInvalid(resourceName, ex);
                }
            }

            if (loaded > 0)
            {
                logger.EmbeddedFallbacksLoaded(loaded, assembly.GetName().Name ?? "(unknown)");
            }
        }

        return prompts;
    }

    private static IEnumerable<string> GetPromptResourceNames(Assembly assembly)
    {
        string[] names;
        try
        {
            names = assembly.GetManifestResourceNames();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException)
        {
            // Dynamic or collectible assemblies cannot enumerate resources; they simply have none.
            return [];
        }

        return names.Where(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Derives a prompt name from a resource name, used only when the YAML omits <c>name:</c>.
    /// <c>MyApp.Prompts.RefundAgent.prompt.yaml</c> becomes <c>RefundAgent</c>.
    /// </summary>
    internal static string DeriveName(string resourceName)
    {
        var withoutSuffix = resourceName[..^ResourceSuffix.Length];
        var lastDot = withoutSuffix.LastIndexOf('.');
        return lastDot < 0 ? withoutSuffix : withoutSuffix[(lastDot + 1)..];
    }
}
