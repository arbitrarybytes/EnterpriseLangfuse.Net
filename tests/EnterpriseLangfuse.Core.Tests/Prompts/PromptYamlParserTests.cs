using EnterpriseLangfuse.Shared;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests.Prompts;

public sealed class PromptYamlParserTests
{
    [Fact]
    public void Parses_a_chat_prompt()
    {
        var document = PromptYamlParser.Parse(
            """
            name: RefundAgent
            type: chat
            labels: [production, latest]
            tags: [support]
            config:
              model: claude-opus-4-8
              temperature: 0.2
            messages:
              - role: system
                content: You are helpful.
              - role: user
                content: "Hi {{customerName}}"
            """);

        document.Name.ShouldBe("RefundAgent");
        document.Kind.ShouldBe(PromptKind.Chat);
        document.Messages.Count.ShouldBe(2);
        document.Messages[0].Role.ShouldBe("system");
        document.Messages[1].Content.ShouldBe("Hi {{customerName}}");
        document.Labels.ShouldBe(["production", "latest"]);
        document.Tags.ShouldBe(["support"]);
        document.Variables.ShouldBe(["customerName"]);
    }

    [Fact]
    public void Parses_a_text_prompt()
    {
        var document = PromptYamlParser.Parse(
            """
            name: Greeter
            type: text
            prompt: "Hello {{name}}"
            """);

        document.Kind.ShouldBe(PromptKind.Text);
        document.Text.ShouldBe("Hello {{name}}");
        document.Variables.ShouldBe(["name"]);
    }

    [Fact]
    public void Infers_chat_from_the_presence_of_messages()
    {
        // Simple files should not need ceremony; the shape is unambiguous.
        var document = PromptYamlParser.Parse(
            """
            name: Inferred
            messages:
              - role: user
                content: hi
            """);

        document.Kind.ShouldBe(PromptKind.Chat);
    }

    [Fact]
    public void Infers_text_when_only_a_body_is_present()
    {
        var document = PromptYamlParser.Parse(
            """
            name: Inferred
            prompt: hi
            """);

        document.Kind.ShouldBe(PromptKind.Text);
    }

    [Fact]
    public void Falls_back_to_the_file_name_when_name_is_omitted()
    {
        var document = PromptYamlParser.Parse("prompt: hi", fallbackName: "FromFile");

        document.Name.ShouldBe("FromFile");
    }

    [Fact]
    public void Preserves_the_internal_line_breaks_of_a_block_scalar()
    {
        // Block scalars are how real multi-line prompts are written; mangling them changes model
        // behaviour. Line breaks are normalised to \n regardless of the file's CRLF/LF endings, so a
        // prompt renders identically whichever platform committed it.
        var document = PromptYamlParser.Parse("name: Block\r\nprompt: |\r\n  Line one.\r\n  Line two.\r\n");

        document.Text.ShouldBe("Line one.\nLine two.\n");
    }

    [Fact]
    public void Applies_yaml_clip_chomping_to_a_block_scalar()
    {
        // `|` keeps a single trailing newline when the source has one and none when it does not.
        // Asserted explicitly so a future parser swap cannot silently change prompt whitespace.
        PromptYamlParser.Parse("name: B\nprompt: |\n  Line one.\n").Text.ShouldBe("Line one.\n");
        PromptYamlParser.Parse("name: B\nprompt: |-\n  Line one.\n").Text.ShouldBe("Line one.");
    }

    [Fact]
    public void Reads_config_values_with_their_natural_types()
    {
        var document = PromptYamlParser.Parse(
            """
            name: Typed
            prompt: hi
            config:
              model: claude-opus-4-8
              temperature: 0.2
              max_tokens: 1024
              stream: true
              stop: null
            """);

        document.Config["model"].ShouldBe("claude-opus-4-8");
        document.Config["temperature"].ShouldBe(0.2);
        document.Config["max_tokens"].ShouldBe(1024L);
        document.Config["stream"].ShouldBe(true);
        document.Config["stop"].ShouldBeNull();
    }

    [Fact]
    public void Keeps_a_quoted_number_as_a_string()
    {
        // `version: "1.0"` must not silently become the double 1.0.
        var document = PromptYamlParser.Parse(
            """
            name: Quoted
            prompt: hi
            config:
              version: "1.0"
            """);

        document.Config["version"].ShouldBe("1.0");
    }

    [Fact]
    public void Accepts_a_single_scalar_label()
    {
        var document = PromptYamlParser.Parse(
            """
            name: One
            prompt: hi
            labels: production
            """);

        document.Labels.ShouldBe(["production"]);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("just a string", "mapping")]
    [InlineData("name: NoBody", "body")]
    [InlineData("name: X\ntype: bogus\nprompt: hi", "unknown type")]
    [InlineData("name: X\ntype: chat", "messages")]
    [InlineData("name: X\ntype: chat\nmessages: []", "at least one message")]
    [InlineData("name: X\ntype: chat\nmessages:\n  - content: no role", "role")]
    [InlineData("name: X\ntype: chat\nmessages:\n  - role: user", "content")]
    [InlineData("prompt: hi", "must declare 'name'")]
    [InlineData("name: X\nprompt: hi\nconfig: not-a-mapping", "config")]
    public void Rejects_malformed_prompt_files_with_an_actionable_message(string yaml, string expectedFragment)
    {
        // These messages surface as generator diagnostics and MSBuild errors, so they must say
        // what is wrong, not just that something is.
        var error = Should.Throw<PromptYamlException>(() => PromptYamlParser.Parse(yaml));

        error.Message.ShouldContain(expectedFragment, Case.Insensitive);
    }

    [Fact]
    public void Rejects_yaml_that_is_not_well_formed()
    {
        var error = Should.Throw<PromptYamlException>(() => PromptYamlParser.Parse("name: [unclosed"));

        error.Message.ShouldContain("well-formed");
    }
}
