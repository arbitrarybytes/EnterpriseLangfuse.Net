using EnterpriseLangfuse.Generators.Tests.Infrastructure;

namespace EnterpriseLangfuse.Generators.Tests;

/// <summary>Snapshots the emitted source, per Spec section 4.</summary>
public sealed class PromptGeneratorSnapshotTests
{
    [Fact]
    public Task TextPrompt()
    {
        var driver = GeneratorHarness.Run(("/prompts/Greeter.prompt.yaml", """
            name: Greeter
            type: text
            labels:
              - production
            prompt: "Hello {{name}}, welcome to {{company}}!"
            """));

        return Verify(driver);
    }

    [Fact]
    public Task ChatPrompt()
    {
        var driver = GeneratorHarness.Run(("/prompts/RefundAgent.prompt.yaml", """
            name: RefundAgent
            type: chat
            config:
              model: claude-opus-4-8
            messages:
              - role: system
                content: "You are a refund agent."
              - role: user
                content: "Customer {{customerName}} is asking about order {{orderId}}."
            """));

        return Verify(driver);
    }

    [Fact]
    public Task PromptWithoutVariables()
    {
        var driver = GeneratorHarness.Run(("/prompts/Static.prompt.yaml", """
            name: Static
            prompt: "Answer concisely."
            """));

        return Verify(driver);
    }

    /// <summary>Name and variables both need sanitising, and one variable is a C# keyword.</summary>
    [Fact]
    public Task NamesRequiringSanitisation()
    {
        var driver = GeneratorHarness.Run(("/prompts/support-triage.v2.prompt.yaml", """
            name: support triage.v2
            prompt: "Route {{class}} tickets for {{ label }} using {{customer_tier}}."
            """));

        return Verify(driver);
    }

    /// <summary>The prompt name comes from the file when the document omits <c>name:</c>.</summary>
    [Fact]
    public Task NameInferredFromFileName()
    {
        var driver = GeneratorHarness.Run(("/prompts/Unnamed.prompt.yaml", """
            prompt: "Nothing to see here."
            """));

        return Verify(driver);
    }

    [Fact]
    public Task MultiplePromptsShareOneGeneratedFile()
    {
        var driver = GeneratorHarness.Run(
            ("/prompts/Greeter.prompt.yaml", "name: Greeter\nprompt: \"Hi {{name}}.\""),
            ("/prompts/Farewell.prompt.yaml", "name: Farewell\nprompt: \"Bye {{name}}.\""));

        return Verify(driver);
    }

    /// <summary>Files that are not prompts must be ignored rather than parsed and rejected.</summary>
    [Fact]
    public Task UnrelatedAdditionalFilesProduceNoOutput()
    {
        var driver = GeneratorHarness.Run(("/config/appsettings.json", "{}"));

        return Verify(driver);
    }
}
