using System.Collections.Immutable;
using EnterpriseLangfuse.Generators.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace EnterpriseLangfuse.Generators.Tests;

/// <summary>
/// A generator that throws surfaces in the consumer's IDE as an unactionable CS8785 and takes the
/// whole compilation with it. Every case here must therefore report, not throw.
/// </summary>
public sealed class PromptGeneratorDiagnosticTests
{
    [Theory]
    [InlineData("prompt: [", "not well-formed")]
    [InlineData("", "empty")]
    [InlineData("- just\n- a\n- list", "must be a mapping")]
    [InlineData("name: Broken\ntype: audio\nprompt: hi", "unknown type")]
    [InlineData("name: Broken\ntype: chat", "must declare a 'messages' sequence")]
    [InlineData("name: Broken\ntype: chat\nmessages:\n  - role: user", "has no 'content'")]
    [InlineData("name: Broken\nprompt: hi\nlabels:\n  env: prod", "must be a scalar or a sequence")]
    [InlineData("name: Broken\nprompt: hi\nconfig: nope", "'config' must be a mapping")]
    public void MalformedYaml_ReportsElf001AndDoesNotThrow(string yaml, string expectedMessageFragment)
    {
        var diagnostics = RunAndCollect(("/prompts/Broken.prompt.yaml", yaml));

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("ELF001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain(expectedMessageFragment);
        diagnostic.Location.GetLineSpan().Path.ShouldBe("/prompts/Broken.prompt.yaml");
    }

    /// <summary>A broken file must not deny code generation to the prompts that are fine.</summary>
    [Fact]
    public void MalformedYaml_StillEmitsTheValidPrompts()
    {
        GeneratorHarness.Run(
            out var output,
            ("/prompts/Broken.prompt.yaml", "prompt: ["),
            ("/prompts/Greeter.prompt.yaml", "name: Greeter\nprompt: \"Hi {{name}}.\""));

        var generated = output.SyntaxTrees.Select(t => t.ToString()).ShouldHaveSingleItem();
        generated.ShouldContain("GetGreeterPromptAsync");
    }

    [Fact]
    public void PromptNameWithNoUsableCharacters_ReportsElf001()
    {
        var diagnostics = RunAndCollect(("/prompts/Odd.prompt.yaml", "name: \"!!!\"\nprompt: hi"));

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe("ELF001");
    }

    [Fact]
    public void TwoPromptsCollapsingToOneMethodName_ReportsElf002AndEmitsTheFirst()
    {
        var diagnostics = RunAndCollect(
            out var output,
            ("/prompts/a-hyphenated.prompt.yaml", "name: refund-agent\nprompt: \"A {{x}}\""),
            ("/prompts/z-spaced.prompt.yaml", "name: refund agent\nprompt: \"Z {{y}}\""));

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("ELF002");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().ShouldContain("GetRefundAgentPromptAsync");

        // Ordered by path, so the 'a-' file wins and the choice is stable across machines.
        var generated = output.SyntaxTrees.Select(t => t.ToString()).ShouldHaveSingleItem();
        generated.ShouldContain("string x");
        generated.ShouldNotContain("string y");
    }

    [Fact]
    public void NoPromptFiles_EmitsNothingAndReportsNothing()
    {
        var diagnostics = RunAndCollect(out var output);

        diagnostics.ShouldBeEmpty();
        output.SyntaxTrees.ShouldBeEmpty();
    }

    private static ImmutableArray<Diagnostic> RunAndCollect(params (string Path, string Yaml)[] prompts) =>
        RunAndCollect(out _, prompts);

    private static ImmutableArray<Diagnostic> RunAndCollect(out Compilation output, params (string Path, string Yaml)[] prompts) =>
        GeneratorHarness.Run(out output, prompts)
            .GetRunResult()
            .Diagnostics;
}
