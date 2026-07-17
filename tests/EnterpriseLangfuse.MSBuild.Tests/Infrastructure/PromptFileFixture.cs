using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace EnterpriseLangfuse.MSBuild.Tests.Infrastructure;

/// <summary>
/// A disposable scratch directory for writing <c>.prompt.yaml</c> files.
/// </summary>
/// <remarks>
/// The task reads from disk by design (it is fed MSBuild items, which are paths), so the file system
/// is part of what is under test and stubbing it away would test something else.
/// </remarks>
internal sealed class PromptFileFixture : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("langfuse-msbuild-tests").FullName;

    /// <summary>Writes a prompt file and returns it as the MSBuild item the task would receive.</summary>
    public ITaskItem Write(string fileName, string yaml)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, yaml);
        return new TaskItem(path);
    }

    /// <summary>Creates an empty child directory and returns its full path.</summary>
    public string CreateSubdirectory(string name) =>
        Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a passing test over.
        }
    }
}
