using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using EnterpriseLangfuse.Shared;
using Microsoft.CodeAnalysis;

namespace EnterpriseLangfuse.Generators;

/// <summary>
/// Generates a strongly typed <c>IPromptProvider</c> extension method for every <c>.prompt.yaml</c>
/// file passed to the compiler as an <c>AdditionalFiles</c> item.
/// </summary>
/// <remarks>
/// Every <c>{{mustache}}</c> variable becomes a required <see cref="string"/> parameter, which moves
/// "you forgot to bind a variable" from a runtime exception on a production LLM call to a compile
/// error at the call site.
/// <para>
/// The pipeline is built off <c>AdditionalTextsProvider</c> alone. It deliberately never touches
/// <c>CompilationProvider</c>: the <c>Compilation</c> is a new object on every keystroke, so any
/// stage that consumes it is re-executed for every edit anywhere in the solution and pins the old
/// compilation in memory until it does. This is the same discipline
/// <c>ForAttributeWithMetadataName</c> exists to enforce for syntax-driven generators — the file is
/// parsed once into an equatable <see cref="PromptModel"/>, and every later stage compares models
/// rather than compiler objects.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class PromptGenerator : IIncrementalGenerator
{
    private const string PromptFileSuffix = ".prompt.yaml";

    /// <summary>Wires the prompt files into the compilation.</summary>
    /// <param name="context">The generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(PromptFileSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => Parse(file, cancellationToken));

        // Collect() is what lets duplicate method names be detected at all — the alternative,
        // emitting one file per prompt, is more granular but cannot see a collision until the C#
        // compiler reports an unexplained CS0111 in generated code. The YAML parse above stays
        // per-file and cached, so this stage only re-sorts models that are already in hand.
        context.RegisterSourceOutput(models.Collect(), static (production, all) => Execute(production, all));
    }

    /// <summary>
    /// Reads and validates one prompt file. Never throws for content reasons: a malformed file
    /// yields a model carrying a <see cref="PromptDiagnostic"/> instead.
    /// </summary>
    private static PromptModel Parse(AdditionalText file, CancellationToken cancellationToken)
    {
        var text = file.GetText(cancellationToken)?.ToString();
        if (text is null)
        {
            return Invalid(file.Path, "the file could not be read.");
        }

        // A file named RefundAgent.prompt.yaml is a complete prompt definition even without `name:`.
        var fallbackName = Path.GetFileName(file.Path);
        fallbackName = fallbackName.Substring(0, fallbackName.Length - PromptFileSuffix.Length);

        PromptDocument document;
        try
        {
            document = PromptYamlParser.Parse(text, fallbackName);
        }
        catch (PromptYamlException ex)
        {
            return Invalid(file.Path, ex.Message);
        }
#pragma warning disable CA1031 // Any unforeseen parser failure must still surface as a diagnostic:
        catch (Exception ex)   // an escaping exception becomes a CS8785 with a stack trace in the user's IDE.
#pragma warning restore CA1031
        {
            return Invalid(file.Path, ex.Message);
        }

        var methodName = Naming.MethodName(document.Name);
        if (methodName is null)
        {
            return Invalid(file.Path, $"prompt name '{document.Name}' contains no characters usable in a C# method name.");
        }

        return new PromptModel(
            file.Path,
            document.Name,
            methodName,
            Naming.Parameters(document.Variables),
            Error: null);
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<PromptModel> models)
    {
        var emitted = new List<PromptModel>(models.Length);
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);

        // Ordered by path so which of two colliding files "wins" does not depend on the file system's
        // enumeration order — an unstable choice would make the generated API flap between machines.
        foreach (var model in models.OrderBy(static m => m.FilePath, StringComparer.Ordinal))
        {
            if (model.Error is { } error)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    PromptDiagnostics.Descriptor(error.Descriptor),
                    PromptDiagnostics.FileLocation(error.FilePath),
                    error.Message));
                continue;
            }

            if (claimed.ContainsKey(model.MethodName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    PromptDiagnostics.DuplicatePromptName,
                    PromptDiagnostics.FileLocation(model.FilePath),
                    model.MethodName));
                continue;
            }

            claimed.Add(model.MethodName, model.FilePath);
            emitted.Add(model);
        }

        if (emitted.Count == 0)
        {
            return;
        }

        context.AddSource(PromptSourceEmitter.HintName, PromptSourceEmitter.Emit(emitted));
    }

    private static PromptModel Invalid(string path, string message) =>
        new(
            path,
            PromptName: string.Empty,
            MethodName: string.Empty,
            Variables: default,
            Error: new PromptDiagnostic(PromptDiagnosticKind.InvalidPromptYaml, path, message));
}
