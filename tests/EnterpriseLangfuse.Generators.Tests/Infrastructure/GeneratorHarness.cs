using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace EnterpriseLangfuse.Generators.Tests.Infrastructure;

/// <summary>An <see cref="AdditionalText"/> backed by a string, standing in for a file on disk.</summary>
public sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) =>
        SourceText.From(content, Encoding.UTF8);
}

/// <summary>Runs <see cref="PromptGenerator"/> against a synthetic compilation.</summary>
public static class GeneratorHarness
{
    /// <summary>
    /// Every assembly the runtime loaded us with, so generated code can bind against the real Core
    /// types rather than a hand-listed subset that drifts as Core's dependencies change.
    /// </summary>
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static GeneratorDriver Run(params (string Path, string Yaml)[] prompts) =>
        Run(out _, prompts);

    public static GeneratorDriver Run(out Compilation output, params (string Path, string Yaml)[] prompts)
    {
        var compilation = CSharpCompilation.Create(
            "ConsumerUnderTest",
            syntaxTrees: [],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new PromptGenerator())
            .AddAdditionalTexts([.. prompts.Select(p => (AdditionalText)new InMemoryAdditionalText(p.Path, p.Yaml))]);

        return driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        var paths = trusted
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // The test host may have loaded Core from outside the TPA list; make sure it is present exactly once.
        var core = typeof(EnterpriseLangfuse.Prompts.IPromptProvider).Assembly.Location;
        if (!paths.Contains(core, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(core);
        }

        return [.. paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }
}
