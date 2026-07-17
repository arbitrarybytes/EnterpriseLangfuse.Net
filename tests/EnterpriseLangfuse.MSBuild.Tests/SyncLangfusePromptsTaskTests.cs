using System.Net;
using EnterpriseLangfuse.MSBuild.Tests.Infrastructure;
using Microsoft.Build.Framework;
using Shouldly;

namespace EnterpriseLangfuse.MSBuild.Tests;

public sealed class SyncLangfusePromptsTaskTests : IDisposable
{
    private const string TextPromptYaml = """
        name: Greeter
        prompt: Hello {{name}}
        labels:
          - staging
        config:
          model: gpt-4o
          temperature: 0.2
        """;

    private const string ChatPromptYaml = """
        name: Chatty
        type: chat
        messages:
          - role: system
            content: You are {{persona}}.
          - role: user
            content: "{{question}}"
        """;

    private readonly PromptFileFixture _files = new();
    private readonly FakeBuildEngine _engine = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Execute_WithDryRun_MakesNoNetworkCall()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.DryRun = true;

        var succeeded = task.Execute();

        succeeded.ShouldBeTrue();
        handler.CallCount.ShouldBe(0);
        task.SyncedPromptCount.ShouldBe(1);
        _engine.MessageText.ShouldContain("[DryRun]");
        _engine.MessageText.ShouldContain("Greeter");
    }

    [Fact]
    public void Execute_WithMalformedYaml_LogsErrorAndReturnsFalseWithoutThrowing()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("broken.prompt.yaml", "name: Broken\n\tprompt: [unclosed"));

        var succeeded = Should.NotThrow(task.Execute);

        succeeded.ShouldBeFalse();
        _engine.Errors.ShouldHaveSingleItem();
        _engine.Errors[0].File.ShouldEndWith("broken.prompt.yaml");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public void Execute_WithSchemaViolation_LogsErrorAndReturnsFalse()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("bodyless.prompt.yaml", "name: Bodyless\nlabels: [production]"));

        task.Execute().ShouldBeFalse();

        _engine.ErrorText.ShouldContain("must declare a 'prompt' body");
    }

    [Fact]
    public void Execute_WhenOneFileIsInvalid_PublishesNothing()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(
            handler,
            _files.Write("greeter.prompt.yaml", TextPromptYaml),
            _files.Write("broken.prompt.yaml", "name: Broken\ntype: nonsense\nprompt: x"));

        task.Execute().ShouldBeFalse();

        // The valid file must not be published: a half-applied commit is worse than a failed one.
        handler.CallCount.ShouldBe(0);
        task.SyncedPromptCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(null, "sk-lf-secret", "PublicKey")]
    [InlineData("pk-lf-public", null, "SecretKey")]
    [InlineData(null, null, "PublicKey and SecretKey")]
    public void Execute_WithMissingCredentials_LogsActionableErrorAndReturnsFalse(
        string? publicKey,
        string? secretKey,
        string expectedSubject)
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.PublicKey = publicKey;
        task.SecretKey = secretKey;

        task.Execute().ShouldBeFalse();

        _engine.ErrorText.ShouldContain(expectedSubject);
        _engine.ErrorText.ShouldContain("LANGFUSE_PUBLIC_KEY");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public void Execute_WithoutCredentialProperties_FallsBackToEnvironmentVariables()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.PublicKey = null;
        task.SecretKey = null;
        task.EnvironmentReader = name => name switch
        {
            "LANGFUSE_PUBLIC_KEY" => "pk-lf-from-env",
            "LANGFUSE_SECRET_KEY" => "sk-lf-from-env",
            _ => null,
        };

        task.Execute().ShouldBeTrue();

        task.SyncedPromptCount.ShouldBe(1);
        _engine.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Execute_PrefersCredentialPropertiesOverEnvironmentVariables()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        // A user who passed properties explicitly said which project to publish to; the ambient
        // environment must not silently redirect that.
        task.EnvironmentReader = _ => "sk-lf-ambient";

        task.Execute().ShouldBeTrue();

        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("pk-lf-public:sk-lf-secret"));
        handler.Requests.ShouldHaveSingleItem().Authorization.ShouldBe(expected);
    }

    [Fact]
    public void Execute_NeverLogsCredentialValues()
    {
        var handler = StubHttpMessageHandler.AlwaysFails(HttpStatusCode.Unauthorized);
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));

        task.Execute().ShouldBeFalse();

        var everythingLogged = string.Join(
            Environment.NewLine,
            _engine.ErrorText,
            _engine.MessageText,
            string.Join(Environment.NewLine, _engine.Warnings.Select(warning => warning.Message)));

        everythingLogged.ShouldNotContain("pk-lf-public");
        everythingLogged.ShouldNotContain("sk-lf-secret");
    }

    [Fact]
    public void Execute_WithNoPromptFiles_Succeeds()
    {
        var task = CreateTask(StubHttpMessageHandler.AlwaysSucceeds());

        task.Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Execute_WithInvalidBaseUrl_LogsErrorAndReturnsFalse()
    {
        var task = CreateTask(StubHttpMessageHandler.AlwaysSucceeds(), _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.BaseUrl = "not-a-url";

        task.Execute().ShouldBeFalse();

        _engine.ErrorText.ShouldContain("LangfuseBaseUrl");
    }

    [Fact]
    public void Execute_PublishesTextPromptTaggedWithCommitAndUnionedLabels()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.Labels = ["production"];
        task.CommitMessage = "release 1.2.3";

        task.Execute().ShouldBeTrue();

        task.SyncedPromptCount.ShouldBe(1);
        var body = handler.Requests.ShouldHaveSingleItem().Body!;
        body.ShouldContain("\"name\":\"Greeter\"");
        body.ShouldContain("\"prompt\":\"Hello {{name}}\"");
        body.ShouldContain("\"type\":\"text\"");
        body.ShouldContain($"\"tags\":[\"git:{FakeCommitHash}\"]");
        body.ShouldContain("\"labels\":[\"staging\",\"production\"]");
        body.ShouldContain("\"commitMessage\":\"release 1.2.3\"");
        body.ShouldContain("\"config\":{\"model\":\"gpt-4o\",\"temperature\":0.2}");
    }

    [Fact]
    public void Execute_PublishesChatPromptAsMessageArray()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("chatty.prompt.yaml", ChatPromptYaml));

        task.Execute().ShouldBeTrue();

        var body = handler.Requests.ShouldHaveSingleItem().Body!;
        body.ShouldContain("\"type\":\"chat\"");
        body.ShouldContain("\"prompt\":[{\"role\":\"system\",\"content\":\"You are {{persona}}.\"}");
        body.ShouldContain("{\"role\":\"user\",\"content\":\"{{question}}\"}");
    }

    [Fact]
    public void Execute_WithoutResolvableCommit_PublishesUntaggedRatherThanFailing()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.CommitHash = null;
        // Points 'git rev-parse' at a real directory that is not a work tree, standing in for a
        // source tarball or Docker context with no .git at all.
        task.ProjectDirectory = _files.CreateSubdirectory("no-repo-here");

        task.Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
        handler.Requests.ShouldHaveSingleItem().Body!.ShouldNotContain("\"tags\"");
        _engine.MessageText.ShouldContain("without a commit tag");
    }

    [Fact]
    public void Execute_WithoutProjectDirectory_FallsBackToTheEngineProjectFile()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));
        task.CommitHash = null;
        task.ProjectDirectory = null;
        // For NuGet consumers ProjectFileOfTaskNode is the packaged .targets, which is why the
        // .targets passes ProjectDirectory explicitly; the engine path remains for hand-wired callers.
        _engine.ProjectFileOfTaskNode = Path.Combine(_files.CreateSubdirectory("engine-fallback"), "App.csproj");

        task.Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
        _engine.MessageText.ShouldContain("without a commit tag");
    }

    [Fact]
    public void Execute_WhenLangfuseRejectsCredentials_LogsRemediationAndReturnsFalse()
    {
        var handler = StubHttpMessageHandler.AlwaysFails(HttpStatusCode.Unauthorized);
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));

        Should.NotThrow(task.Execute).ShouldBeFalse();

        _engine.ErrorText.ShouldContain("401");
        _engine.ErrorText.ShouldContain("credentials were rejected");
    }

    [Fact]
    public void Execute_WhenLangfuseIsUnreachable_LogsErrorAndReturnsFalse()
    {
        var handler = StubHttpMessageHandler.AlwaysThrows(new HttpRequestException("no such host"));
        var task = CreateTask(handler, _files.Write("greeter.prompt.yaml", TextPromptYaml));

        Should.NotThrow(task.Execute).ShouldBeFalse();

        _engine.ErrorText.ShouldContain("Could not reach Langfuse");
    }

    [Fact]
    public void Execute_WithDuplicatePromptNames_LogsErrorAndReturnsFalse()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(
            handler,
            _files.Write("a.prompt.yaml", TextPromptYaml),
            _files.Write("b.prompt.yaml", TextPromptYaml));

        task.Execute().ShouldBeFalse();

        _engine.ErrorText.ShouldContain("must be unique");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public void Execute_WithoutDeclaredName_DerivesNameFromFile()
    {
        var handler = StubHttpMessageHandler.AlwaysSucceeds();
        var task = CreateTask(handler, _files.Write("RefundAgent.prompt.yaml", "prompt: Refund {{orderId}}"));

        task.Execute().ShouldBeTrue();

        handler.Requests.ShouldHaveSingleItem().Body!.ShouldContain("\"name\":\"RefundAgent\"");
    }

    private const string FakeCommitHash = "0123456789abcdef0123456789abcdef01234567";

    private SyncLangfusePromptsTask CreateTask(StubHttpMessageHandler handler, params ITaskItem[] promptFiles) =>
        new()
        {
            BuildEngine = _engine,
            PromptFiles = promptFiles,
            PublicKey = "pk-lf-public",
            SecretKey = "sk-lf-secret",
            BaseUrl = "https://langfuse.example.com",
            // Pinned rather than discovered: shelling out to git would make every test depend on the
            // machine's checkout state and on git being installed.
            CommitHash = FakeCommitHash,
            HttpMessageHandlerFactory = () => handler,
            // The task reads LANGFUSE_* variables when credentials are unset; the machine's real
            // environment must not decide whether the missing-credential tests pass.
            EnvironmentReader = _ => null,
        };
}
