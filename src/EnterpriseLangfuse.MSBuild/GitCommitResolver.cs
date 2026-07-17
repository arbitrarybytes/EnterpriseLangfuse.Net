using System.Diagnostics;

namespace EnterpriseLangfuse.MSBuild;

/// <summary>
/// Resolves the Git commit hash of the build being synced, best-effort.
/// </summary>
/// <remarks>
/// Deliberately has no LibGit2Sharp dependency: that would add a native, platform-specific payload to
/// a package whose only job is to shell out once per build. CI environments already publish the hash
/// as a variable, and everywhere else <c>git</c> is on PATH, so the cost of a real Git library buys
/// nothing here.
/// <para>
/// Every failure path returns <see langword="null"/> rather than throwing. A missing commit hash means
/// one absent tag on a prompt; it must never be the reason a build fails — source trees are routinely
/// built from tarballs, Docker contexts and shallow exports with no <c>.git</c> at all.
/// </para>
/// </remarks>
internal static class GitCommitResolver
{
    /// <summary>
    /// Variables published by the major CI providers, in order: GitHub Actions, Azure Pipelines,
    /// GitLab CI, Jenkins, TeamCity. Preferred over the Git CLI because a CI checkout may be detached
    /// or shallow in ways that make the working tree's HEAD less authoritative than the trigger.
    /// </summary>
    private static readonly string[] CommitEnvironmentVariables =
    [
        "GITHUB_SHA",
        "BUILD_SOURCEVERSION",
        "CI_COMMIT_SHA",
        "GIT_COMMIT",
        "BUILD_VCS_NUMBER",
    ];

    /// <summary>Bounds a hung <c>git</c> (for example one prompting for credentials) to a build-safe delay.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <param name="workingDirectory">Directory to run <c>git</c> in; typically the project directory.</param>
    /// <param name="environmentReader">Reads an environment variable. Injected so tests need not mutate process state.</param>
    /// <returns>A 40-character commit hash, or <see langword="null"/> when none can be determined.</returns>
    public static string? Resolve(string? workingDirectory, Func<string, string?> environmentReader)
    {
        foreach (var variable in CommitEnvironmentVariables)
        {
            if (TryNormalize(environmentReader(variable), out var fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        return TryNormalize(RunGitRevParse(workingDirectory), out var fromGit) ? fromGit : null;
    }

    private static string? RunGitRevParse(string? workingDirectory)
    {
        // A directory that was asked for but does not exist must not silently fall back to the
        // process working directory: whatever repository that happens to be in is not the project
        // being built, and a plausible-looking hash from the wrong repository is worse than none.
        if (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            return null;
        }

        // Launched by full path rather than as bare "git": Win32 process creation searches the
        // application and current directories before PATH, so an unqualified name would execute a
        // git.exe planted in the build's working directory.
        var gitPath = FindGitOnPath();
        if (gitPath is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(gitPath, "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Started before waiting: git writes to both pipes, and letting either fill its buffer
            // while we block on WaitForExit would deadlock the build.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? standardOutput.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Win32Exception when git is not installed, IOException on a broken pipe, and any number
            // of platform-specific failures besides. None of them are worth a build's attention.
            return null;
        }
    }

    /// <summary>Locates the <c>git</c> executable by walking PATH entries explicitly.</summary>
    private static string? FindGitOnPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";

        foreach (var entry in pathVariable!.Split(Path.PathSeparator))
        {
            // Windows PATH entries are sometimes quoted; an empty entry would resolve to the
            // current directory, which is exactly the search this method exists to avoid.
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is the machine's problem, not this build's.
            }
        }

        return null;
    }

    /// <summary>
    /// Accepts only a full hex SHA-1. A variable such as <c>GIT_COMMIT=$(something-unset)</c> or a
    /// <c>git</c> error leaking to stdout would otherwise become a nonsense tag in Langfuse.
    /// </summary>
    private static bool TryNormalize(string? candidate, out string? commitHash)
    {
        commitHash = null;

        var trimmed = candidate?.Trim();
        if (trimmed is not { Length: 40 })
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        commitHash = trimmed.ToLowerInvariant();
        return true;
    }
}
