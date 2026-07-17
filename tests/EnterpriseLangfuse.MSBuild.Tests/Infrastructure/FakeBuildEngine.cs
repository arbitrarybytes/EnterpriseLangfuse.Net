using System.Collections;
using Microsoft.Build.Framework;

namespace EnterpriseLangfuse.MSBuild.Tests.Infrastructure;

/// <summary>
/// A minimal <see cref="IBuildEngine"/> that records what a task logged.
/// </summary>
/// <remarks>
/// The engine is the only observable surface a task has — a task returns a bool and logs everything
/// else — so capturing these events is how the contract in requirement "never throw, log instead"
/// gets asserted at all.
/// </remarks>
internal sealed class FakeBuildEngine : IBuildEngine
{
    public List<BuildErrorEventArgs> Errors { get; } = [];

    public List<BuildWarningEventArgs> Warnings { get; } = [];

    public List<BuildMessageEventArgs> Messages { get; } = [];

    public bool ContinueOnError => false;

    public int LineNumberOfTaskNode => 0;

    public int ColumnNumberOfTaskNode => 0;

    public string ProjectFileOfTaskNode { get; set; } = string.Empty;

    public string ErrorText => string.Join(Environment.NewLine, Errors.Select(error => error.Message));

    public string MessageText => string.Join(Environment.NewLine, Messages.Select(message => message.Message));

    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

    public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);

    public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);

    public void LogCustomEvent(CustomBuildEventArgs e)
    {
    }

    public bool BuildProjectFile(
        string projectFileName,
        string[] targetNames,
        IDictionary globalProperties,
        IDictionary targetOutputs) =>
        throw new NotSupportedException("The sync task never builds another project.");
}
