using EnterpriseLangfuse.Prompts;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Prompts;

public sealed class PromptCompilationTests
{
    [Fact]
    public void Substitutes_variables()
    {
        var prompt = TextPrompt("Hello {{name}}, welcome to {{company}}!");

        var compiled = prompt.Compile(new Dictionary<string, object?> { ["name"] = "Ada", ["company"] = "Acme" });

        compiled.Text.ShouldBe("Hello Ada, welcome to Acme!");
    }

    [Fact]
    public void Tolerates_whitespace_inside_the_braces()
    {
        var prompt = TextPrompt("Hello {{ name }}!");

        prompt.Variables.ShouldBe(["name"]);
        prompt.Compile(new Dictionary<string, object?> { ["name"] = "Ada" }).Text.ShouldBe("Hello Ada!");
    }

    [Fact]
    public void Substitutes_every_occurrence_of_a_repeated_variable()
    {
        var prompt = TextPrompt("{{x}} and {{x}} again");

        prompt.Variables.ShouldBe(["x"]);
        prompt.Compile(new Dictionary<string, object?> { ["x"] = "A" }).Text.ShouldBe("A and A again");
    }

    [Fact]
    public void Throws_naming_the_variable_when_a_value_is_missing()
    {
        // Rendering a half-substituted prompt into an LLM is a silent production bug; fail loudly.
        var prompt = TextPrompt("Hello {{name}}");

        var error = Should.Throw<MissingPromptVariableException>(
            () => prompt.Compile(new Dictionary<string, object?>()));

        error.VariableName.ShouldBe("name");
        error.PromptName.ShouldBe("Test");
    }

    [Fact]
    public void Renders_a_null_value_as_empty_rather_than_the_literal_null()
    {
        var prompt = TextPrompt("[{{value}}]");

        prompt.Compile(new Dictionary<string, object?> { ["value"] = null }).Text.ShouldBe("[]");
    }

    [Fact]
    public void Formats_values_invariantly()
    {
        // A German locale would otherwise render 0.2 as "0,2" and change the prompt's meaning.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var prompt = TextPrompt("temp={{t}}");

            prompt.Compile(new Dictionary<string, object?> { ["t"] = 0.2 }).Text.ShouldBe("temp=0.2");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    // Unmatched and non-variable braces must survive verbatim rather than being eaten.
    [InlineData("no variables here", "no variables here")]
    [InlineData("unterminated {{name", "unterminated {{name")]
    [InlineData("{{}}", "{{}}")]
    [InlineData("{{ }}", "{{ }}")]
    [InlineData("{{1bad}}", "{{1bad}}")]
    [InlineData("{{with-dash}}", "{{with-dash}}")]
    [InlineData("json: {\"a\": 1}", "json: {\"a\": 1}")]
    public void Leaves_text_that_is_not_a_variable_untouched(string template, string expected)
    {
        var prompt = TextPrompt(template);

        prompt.Variables.ShouldBeEmpty();
        prompt.Compile().Text.ShouldBe(expected);
    }

    [Theory]
    // Mustache constructs this engine deliberately does not interpret must pass through as literals
    // rather than being mistaken for variables and demanding a value.
    [InlineData("{{#section}}x{{/section}}")]
    [InlineData("{{!comment}}")]
    [InlineData("{{>partial}}")]
    [InlineData("{{&unescaped}}")]
    public void Ignores_unsupported_mustache_constructs(string template)
    {
        var prompt = TextPrompt(template);

        prompt.Variables.ShouldBeEmpty();
        prompt.Compile().Text.ShouldBe(template);
    }

    [Fact]
    public void Compiles_every_message_of_a_chat_prompt()
    {
        var prompt = new LangfusePrompt(
            "Chat",
            1,
            LangfusePromptType.Chat,
            null,
            [
                new LangfuseChatMessage("system", "You are {{persona}}."),
                new LangfuseChatMessage("user", "Hi, I am {{user}}."),
            ],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network);

        prompt.Variables.ShouldBe(["persona", "user"]);

        var compiled = prompt.Compile(new Dictionary<string, object?> { ["persona"] = "helpful", ["user"] = "Ada" });

        compiled.Messages[0].Content.ShouldBe("You are helpful.");
        compiled.Messages[1].Content.ShouldBe("Hi, I am Ada.");
        compiled.Messages[0].Role.ShouldBe("system");
    }

    [Fact]
    public void Reports_a_shared_variable_once_across_messages()
    {
        var prompt = new LangfusePrompt(
            "Chat",
            1,
            LangfusePromptType.Chat,
            null,
            [
                new LangfuseChatMessage("system", "{{shared}}"),
                new LangfuseChatMessage("user", "{{shared}} and {{other}}"),
            ],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.Network);

        prompt.Variables.ShouldBe(["shared", "other"]);
    }

    [Fact]
    public void Compile_rejects_a_null_variable_dictionary()
    {
        Should.Throw<ArgumentNullException>(() => TextPrompt("x").Compile(null!));
    }

    [Fact]
    public void Carries_name_version_and_source_onto_the_compiled_result()
    {
        // Downstream tracing links a generation to its prompt revision using exactly these.
        var prompt = new LangfusePrompt(
            "Refund",
            11,
            LangfusePromptType.Text,
            "hi",
            [],
            [],
            [],
            new Dictionary<string, object?>(),
            PromptSource.EmbeddedFallback);

        var compiled = prompt.Compile();

        compiled.Name.ShouldBe("Refund");
        compiled.Version.ShouldBe(11);
        compiled.Source.ShouldBe(PromptSource.EmbeddedFallback);
        compiled.IsFallback.ShouldBeTrue();
    }

    private static LangfusePrompt TextPrompt(string body) => new(
        "Test",
        1,
        LangfusePromptType.Text,
        body,
        [],
        [],
        [],
        new Dictionary<string, object?>(),
        PromptSource.Network);
}
