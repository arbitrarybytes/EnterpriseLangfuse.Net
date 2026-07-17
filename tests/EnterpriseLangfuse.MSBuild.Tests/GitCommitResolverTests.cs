using Shouldly;

namespace EnterpriseLangfuse.MSBuild.Tests;

public sealed class GitCommitResolverTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("GITHUB_SHA")]
    [InlineData("BUILD_SOURCEVERSION")]
    [InlineData("CI_COMMIT_SHA")]
    [InlineData("GIT_COMMIT")]
    [InlineData("BUILD_VCS_NUMBER")]
    public void Resolve_ReadsTheCommitFromEachSupportedCiVariable(string variable)
    {
        var resolved = GitCommitResolver.Resolve(
            workingDirectory: null,
            name => name == variable ? Sha : null);

        resolved.ShouldBe(Sha);
    }

    [Fact]
    public void Resolve_PrefersGitHubActionsOverOtherProviders()
    {
        const string GitHubSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var resolved = GitCommitResolver.Resolve(
            workingDirectory: null,
            name => name switch
            {
                "GITHUB_SHA" => GitHubSha,
                "CI_COMMIT_SHA" => Sha,
                _ => null,
            });

        resolved.ShouldBe(GitHubSha);
    }

    [Fact]
    public void Resolve_NormalizesSurroundingWhitespaceAndCase()
    {
        var resolved = GitCommitResolver.Resolve(
            workingDirectory: null,
            _ => $"  {Sha.ToUpperInvariant()}\n");

        resolved.ShouldBe(Sha);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HEAD")]
    [InlineData("0123456")]
    [InlineData("not-a-hash-not-a-hash-not-a-hash-not-a-h")]
    public void Resolve_IgnoresValuesThatAreNotFullCommitHashes(string value)
    {
        // An unexpanded variable or a git error on stdout must not become a nonsense tag in Langfuse.
        var resolved = GitCommitResolver.Resolve(NonRepositoryDirectory(), name => name == "GITHUB_SHA" ? value : null);

        resolved.ShouldBeNull();
    }

    [Fact]
    public void Resolve_WithNoCiVariablesAndNoRepository_ReturnsNull()
    {
        var resolved = GitCommitResolver.Resolve(NonRepositoryDirectory(), _ => null);

        resolved.ShouldBeNull();
    }

    [Fact]
    public void Resolve_WithNonexistentWorkingDirectory_ReturnsNull()
    {
        // A directory that does not exist must not fall back to running git in the process working
        // directory — that would tag prompts with whatever repository the build host happens to be in.
        var resolved = GitCommitResolver.Resolve(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            _ => null);

        resolved.ShouldBeNull();
    }

    /// <summary>A real directory guaranteed not to be a git work tree, so 'git rev-parse' must fail.</summary>
    private static string NonRepositoryDirectory() =>
        Directory.CreateTempSubdirectory("langfuse-not-a-repo").FullName;
}
