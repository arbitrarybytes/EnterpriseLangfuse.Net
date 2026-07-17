using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EnterpriseLangfuse.Generators;

/// <summary>
/// The diagnostics this generator reports.
/// </summary>
/// <remarks>
/// A generator that throws takes the whole compilation down and, worse, surfaces in the consumer's
/// IDE as an unactionable CS8785 with a stack trace. Every foreseeable authoring mistake is reported
/// as a diagnostic against the offending file instead, so a bad prompt file reads like any other
/// compile error.
/// </remarks>
internal static class PromptDiagnostics
{
    private const string Category = "EnterpriseLangfuse";

    internal static readonly DiagnosticDescriptor InvalidPromptYaml = new(
        id: "ELF001",
        title: "Invalid prompt file",
        messageFormat: "Prompt file could not be parsed: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A .prompt.yaml file must be well-formed YAML satisfying the EnterpriseLangfuse prompt schema.");

    internal static readonly DiagnosticDescriptor DuplicatePromptName = new(
        id: "ELF002",
        title: "Duplicate generated prompt method",
        messageFormat: "Prompt file maps to method '{0}', which another prompt file already generates; this file is ignored",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two prompt names that differ only in punctuation or casing collapse to the same C# method name. Rename one of them.");

    internal static DiagnosticDescriptor Descriptor(PromptDiagnosticKind kind) => kind switch
    {
        PromptDiagnosticKind.DuplicatePromptName => DuplicatePromptName,
        _ => InvalidPromptYaml,
    };

    /// <summary>
    /// Points a diagnostic at the file as a whole.
    /// </summary>
    /// <remarks>
    /// YamlDotNet reports a line/column on parse failures, but not on the schema failures this
    /// parser raises itself, and a location that is precise for half the rules and wrong for the
    /// other half is worse than a consistent file-level one. The message carries the detail.
    /// </remarks>
    internal static Location FileLocation(string filePath) =>
        Location.Create(
            filePath,
            new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));
}
