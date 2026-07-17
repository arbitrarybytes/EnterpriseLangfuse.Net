using EnterpriseLangfuse.Prompts;
using EnterpriseLangfuse.Shared;
using Shouldly;

namespace EnterpriseLangfuse.Generators.Tests.Consuming;

/// <summary>
/// The end-to-end proof. These call methods that exist only because the generator ran inside a plain
/// <c>dotnet build</c> of this project, having loaded YamlDotNet from the analyzer directory. If that
/// bundling regresses, this file stops compiling — which is exactly the signal wanted, because a
/// generator that fails to load produces no error of its own.
/// </summary>
public sealed class GeneratedPromptUsageTests
{
    [Fact]
    public async Task GeneratedMethod_CompilesPromptWithBoundVariables()
    {
        var provider = new StubPromptProvider();

        var compiled = await provider.GetRefundAgentPromptAsync(
            customerName: "Ada",
            orderId: "A-42",
            cancellationToken: TestContext.Current.CancellationToken);

        compiled.Type.ShouldBe(LangfusePromptType.Chat);
        compiled.Messages[1].Content.ShouldBe("Customer Ada is asking about order A-42.");
    }

    [Fact]
    public async Task GeneratedMethod_PassesNameAndLabelThroughToTheProvider()
    {
        var provider = new StubPromptProvider();

        await provider.GetRefundAgentPromptAsync("Ada", "A-42", "staging", TestContext.Current.CancellationToken);

        provider.RequestedName.ShouldBe("RefundAgent");
        provider.RequestedLabel.ShouldBe("staging");
    }

    [Fact]
    public async Task GeneratedMethod_DefaultsToTheProductionLabel()
    {
        var provider = new StubPromptProvider();

        await provider.GetRefundAgentPromptAsync("Ada", "A-42", cancellationToken: TestContext.Current.CancellationToken);

        provider.RequestedLabel.ShouldBe(LangfuseDefaults.ProductionLabel);
    }

    /// <summary>
    /// <c>order-summary.prompt.yaml</c> declares a <c>{{label}}</c> variable, which would otherwise
    /// collide with the method's own <c>label</c> parameter. That it binds at all is the assertion.
    /// </summary>
    [Fact]
    public async Task GeneratedMethod_RenamesAVariableThatCollidesWithAReservedParameter()
    {
        var provider = new StubPromptProvider();

        var compiled = await provider.GetOrderSummaryPromptAsync(
            orderId: "A-42",
            label2: "web",
            label: "production",
            cancellationToken: TestContext.Current.CancellationToken);

        compiled.Text.ShouldBe("Summarise order A-42 for the web channel.");
    }

    /// <summary>Serves the same YAML the generator read, so the bound variables must line up exactly.</summary>
    private sealed class StubPromptProvider : IPromptProvider
    {
        public string? RequestedName { get; private set; }

        public string? RequestedLabel { get; private set; }

        public Task<LangfusePrompt> GetPromptAsync(
            string name,
            string label = LangfuseDefaults.ProductionLabel,
            CancellationToken cancellationToken = default)
        {
            RequestedName = name;
            RequestedLabel = label;

            var yaml = File.ReadAllText(Path.Combine(PromptDirectory, FileNames[name]));
            var document = PromptYamlParser.Parse(yaml, name);
            return Task.FromResult(LangfusePrompt.FromDocument(document, PromptSource.EmbeddedFallback));
        }

        private static readonly Dictionary<string, string> FileNames = new(StringComparer.Ordinal)
        {
            ["RefundAgent"] = "RefundAgent.prompt.yaml",
            ["order-summary"] = "order-summary.prompt.yaml",
        };

        private static string PromptDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Consuming", "Prompts");
    }
}
