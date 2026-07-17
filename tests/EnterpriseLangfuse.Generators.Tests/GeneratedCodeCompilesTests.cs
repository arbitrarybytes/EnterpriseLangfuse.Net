using EnterpriseLangfuse.Generators.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace EnterpriseLangfuse.Generators.Tests;

/// <summary>
/// A snapshot only proves the generator emits the text it emitted last time. These compile the
/// emitted text against the real Core assembly, which is what catches an identifier that needed
/// escaping, a parameter that collides, or a type name that does not resolve.
/// </summary>
public sealed class GeneratedCodeCompilesTests
{
    [Theory]
    [InlineData("Greeter", "name: Greeter\nprompt: \"Hello {{name}} of {{company}}.\"")]
    [InlineData("keyword variables", "name: Keywords\nprompt: \"{{class}} {{int}} {{return}} {{namespace}}\"")]
    [InlineData("reserved parameter names", "name: Reserved", "prompt: \"{{provider}} {{label}} {{cancellationToken}} {{variables}} {{prompt}}\"")]
    [InlineData("punctuated name", "name: support/triage v2\nprompt: \"Route {{customer_tier}}.\"")]
    [InlineData("underscore variable", "name: Underscored\nprompt: \"{{_leading}} {{trailing_}}\"")]
    [InlineData("quotes in the name", "name: 'He said \"hi\"'\nprompt: \"{{x}}\"")]
    // A block-scalar name embeds real newlines; if the emitter let them through, the tail of the
    // name would land in the generated file as raw code outside the doc comment.
    [InlineData("multi-line block-scalar name", "name: |-", "  Support triage", "  ; System.Environment.Exit(1); //", "prompt: \"{{x}}\"")]
    [InlineData("no variables", "name: Static\nprompt: \"Answer concisely.\"")]
    [InlineData("chat prompt", "name: Chat\ntype: chat\nmessages:\n  - role: user\n    content: \"{{q}}\"")]
    public void GeneratedCode_Compiles(string reason, params string[] yamlLines)
    {
        GeneratorHarness.Run(out var output, ("/prompts/Case.prompt.yaml", string.Join("\n", yamlLines)));

        output.SyntaxTrees.ShouldNotBeEmpty($"nothing was generated for {reason}");

        var errors = output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        errors.ShouldBeEmpty($"generated code for {reason} did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    /// <summary>
    /// The generated methods are public, so they land in the consumer's XML documentation file. A
    /// consumer with <c>GenerateDocumentationFile</c> and warnings-as-errors — this repository, for
    /// one — would fail to build on an undocumented member or a malformed doc comment.
    /// </summary>
    [Fact]
    public void GeneratedCode_CarriesWellFormedXmlDocumentation()
    {
        GeneratorHarness.Run(out var output, ("/prompts/Case.prompt.yaml", "name: 'A & B <C>'\nprompt: \"{{class}} {{x}}\""));

        var tree = output.SyntaxTrees.Single();
        var withDocs = CSharpSyntaxTree.ParseText(
            tree.ToString(),
            new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
            cancellationToken: TestContext.Current.CancellationToken);

        withDocs.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ShouldBeEmpty();
    }
}
