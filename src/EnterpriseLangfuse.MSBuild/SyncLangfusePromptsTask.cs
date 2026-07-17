using EnterpriseLangfuse.Shared;
using Langfuse;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace EnterpriseLangfuse.MSBuild;

/// <summary>
/// Synchronises local, version-controlled <c>.prompt.yaml</c> files to Langfuse, tagging each with the
/// current Git commit hash so a deployed prompt can always be traced back to the source that produced it.
/// </summary>
/// <remarks>
/// This is the write half of Code-as-Truth: the repository is authoritative and Langfuse is a mirror.
/// Langfuse prompt creation is append-only — each sync publishes a new version and moves the given
/// labels onto it — so re-running the task is safe, if not free.
/// <para>
/// Uses the AutoSDK's <c>Prompts.PromptsCreateAsync</c>. That is a narrow exception to this
/// framework's usual avoidance of the AutoSDK's models: <see cref="CreatePromptRequest"/> is a
/// oneOf-style union rather than an <c>AllOf&lt;T1,T2&gt;</c>, so it escapes the converter bug that
/// forces the runtime paths onto hand-written contracts (see the Core project's LangfuseWireModels.cs).
/// </para>
/// </remarks>
public sealed class SyncLangfusePromptsTask : MSBuildTask
{
    /// <summary>Langfuse Cloud, used when <see cref="BaseUrl"/> is not supplied.</summary>
    private const string DefaultBaseUrl = "https://cloud.langfuse.com";

    /// <summary>The <c>.prompt.yaml</c> files to publish. An empty set is a no-op, not an error.</summary>
    public ITaskItem[] PromptFiles { get; set; } = [];

    /// <summary>Langfuse public key (<c>pk-lf-...</c>). Falls back to <c>LANGFUSE_PUBLIC_KEY</c> when unset.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Langfuse secret key (<c>sk-lf-...</c>). Falls back to <c>LANGFUSE_SECRET_KEY</c> when unset.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Langfuse host. Defaults to Langfuse Cloud.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Environment labels to move onto the published version, for example <c>production</c>. Unioned
    /// with any <c>labels:</c> the file itself declares.
    /// </summary>
    public string[] Labels { get; set; } = [];

    /// <summary>
    /// Commit message recorded against every published version. Overrides each file's own
    /// <c>commitMessage:</c>; typically the build's Git subject line.
    /// </summary>
    public string? CommitMessage { get; set; }

    /// <summary>
    /// Parses and validates every prompt and reports what would be published, without any network call.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Overrides Git commit detection. Only needed where neither a CI variable nor a <c>.git</c>
    /// directory is available but the hash is known — a container build fed the SHA as an argument, say.
    /// </summary>
    public string? CommitHash { get; set; }

    /// <summary>
    /// The directory of the project being built, used as the working directory for Git commit
    /// detection. The .targets passes <c>$(MSBuildProjectDirectory)</c>.
    /// </summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>Number of prompts published, or that would have been published under <see cref="DryRun"/>.</summary>
    [Output]
    public int SyncedPromptCount { get; private set; }

    /// <summary>
    /// Supplies the transport. Exists so tests can drive the real AutoSDK serialisation path against a
    /// stub instead of the network; production leaves it null and gets a plain handler.
    /// </summary>
    internal Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    /// <summary>Reads environment variables, injected for the same reason as <see cref="HttpMessageHandlerFactory"/>.</summary>
    internal Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            // No MSBuild task may throw: the host turns any escaped exception into an MSB4018 stack
            // trace that buries the actual problem. Every failure below is a logged error instead,
            // and this catch is the backstop for the ones that were not anticipated.
            return ExecuteCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.LogError($"Langfuse prompt sync failed unexpectedly: {ex.Message}");
            Log.LogMessage(MessageImportance.Low, ex.ToString());
            return false;
        }
    }

    private async Task<bool> ExecuteCoreAsync()
    {
        if (PromptFiles.Length == 0)
        {
            Log.LogMessage(MessageImportance.Low, "No prompt files supplied to Langfuse sync; nothing to do.");
            return true;
        }

        // Credentials are checked even for a dry run, so that a dry run is an honest rehearsal of the
        // real thing rather than a check that passes right up until it matters.
        if (!TryValidateCredentials() || !TryResolveBaseUri(out var baseUri))
        {
            return false;
        }

        if (!TryLoadDocuments(out var documents))
        {
            return false;
        }

        var commitHash = ResolveCommitHash();
        string[] tags = commitHash is null ? [] : [$"git:{commitHash}"];

        if (commitHash is null)
        {
            Log.LogMessage(
                MessageImportance.Normal,
                "No Git commit hash found (no CI variable and no usable 'git rev-parse'); prompts will be synced without a commit tag.");
        }

        if (DryRun)
        {
            foreach (var (document, path) in documents)
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"[DryRun] Would sync prompt '{document.Name}' ({document.Kind.ToString().ToLowerInvariant()}) from '{path}' with labels [{string.Join(", ", ResolveLabels(document))}] and tags [{string.Join(", ", tags)}].");
            }

            SyncedPromptCount = documents.Count;
            return true;
        }

        return await PushAsync(documents, baseUri, tags).ConfigureAwait(false);
    }

    private async Task<bool> PushAsync(
        IReadOnlyList<(PromptDocument Document, string Path)> documents,
        Uri baseUri,
        IReadOnlyList<string> tags)
    {
        using var handler = HttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var client = new LangfuseClient(httpClient, baseUri, disposeHttpClient: false);
        client.AuthorizeUsingBasic(PublicKey!, SecretKey!);

        foreach (var (document, path) in documents)
        {
            var request = BuildRequest(document, ResolveLabels(document), tags, CommitMessage ?? document.CommitMessage);

            try
            {
                await client.Prompts.PromptsCreateAsync(request, cancellationToken: default).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                LogFileError(path, $"Langfuse rejected prompt '{document.Name}' with HTTP {(int)ex.StatusCode} ({ex.StatusCode}). {DescribeApiFailure(ex)}");
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LogFileError(path, $"Could not reach Langfuse at '{baseUri}' to sync prompt '{document.Name}': {ex.Message}. Check the host is reachable from this build agent.");
                return false;
            }

            SyncedPromptCount++;
            Log.LogMessage(MessageImportance.High, $"Synced prompt '{document.Name}' to Langfuse.");
        }

        Log.LogMessage(MessageImportance.High, $"Langfuse sync complete: {SyncedPromptCount} prompt(s) published.");
        return true;
    }

    /// <summary>Turns an AutoSDK failure into something a build log reader can act on.</summary>
    private static string DescribeApiFailure(ApiException exception) => (int)exception.StatusCode switch
    {
        401 or 403 => "The credentials were rejected. Verify LangfusePublicKey/LangfuseSecretKey belong to this project and host.",
        404 => "The endpoint was not found. Verify LangfuseBaseUrl points at a Langfuse instance.",
        _ => exception.ResponseBody ?? exception.Message,
    };

    private static CreatePromptRequest BuildRequest(
        PromptDocument document,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> tags,
        string? commitMessage)
    {
        // Empty collections are sent as null so the field is omitted entirely: an explicit "labels":[]
        // would ask Langfuse to clear labels, which is not what "this file declares none" means.
        var config = document.Config.Count == 0 ? null : document.Config;
        var labelList = labels.Count == 0 ? null : labels.ToList();
        var tagList = tags.Count == 0 ? null : tags.ToList();

        if (document.Kind == PromptKind.Chat)
        {
            return new CreatePromptRequest(new CreateChatPromptRequest
            {
                Name = document.Name,
                Prompt = [.. document.Messages.Select(message =>
                    (ChatMessageWithPlaceholders)new ChatMessage { Role = message.Role, Content = message.Content })],
                Type = CreateChatPromptType.Chat,
                Config = config,
                Labels = labelList,
                Tags = tagList,
                CommitMessage = commitMessage,
            });
        }

        return new CreatePromptRequest(new CreateTextPromptRequest
        {
            Name = document.Name,
            Prompt = document.Text,
            Type = CreateTextPromptType.Text,
            Config = config,
            Labels = labelList,
            Tags = tagList,
            CommitMessage = commitMessage,
        });
    }

    private bool TryValidateCredentials()
    {
        // The environment is the preferred channel for credentials, not merely a fallback: an
        // MSBuild property travels through the engine and is recorded verbatim in binary logs
        // (/bl), which get attached to CI runs and bug reports. Reading the variables here, in
        // the task, keeps the default path from ever surfacing the secret as a property.
        PublicKey = string.IsNullOrWhiteSpace(PublicKey) ? EnvironmentReader("LANGFUSE_PUBLIC_KEY") : PublicKey;
        SecretKey = string.IsNullOrWhiteSpace(SecretKey) ? EnvironmentReader("LANGFUSE_SECRET_KEY") : SecretKey;

        if (!string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(SecretKey))
        {
            return true;
        }

        // Names only — never the values. A build log is not a secret store, and CI logs are widely readable.
        var missing = string.IsNullOrWhiteSpace(PublicKey)
            ? string.IsNullOrWhiteSpace(SecretKey) ? "PublicKey and SecretKey" : "PublicKey"
            : "SecretKey";

        Log.LogError(
            $"Langfuse prompt sync requires {missing}. Set the LangfusePublicKey and LangfuseSecretKey MSBuild properties, " +
            "or the LANGFUSE_PUBLIC_KEY and LANGFUSE_SECRET_KEY environment variables, from your CI secret store. " +
            "Keys are issued under Langfuse project settings.");

        return false;
    }

    private bool TryResolveBaseUri(out Uri baseUri)
    {
        var value = string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl!.Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            Log.LogError($"LangfuseBaseUrl '{value}' is not a valid absolute http(s) URL. Example: {DefaultBaseUrl}");
            baseUri = null!;
            return false;
        }

        // A base address without a trailing slash silently loses the last path segment of a
        // self-hosted URL such as https://host/langfuse when the SDK resolves relative endpoints.
        baseUri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    /// <summary>
    /// Parses every file before any is published, so a typo in the last prompt cannot leave Langfuse
    /// holding half of a commit's worth of changes.
    /// </summary>
    private bool TryLoadDocuments(out IReadOnlyList<(PromptDocument Document, string Path)> documents)
    {
        var loaded = new List<(PromptDocument, string)>(PromptFiles.Length);
        var failed = false;

        foreach (var item in PromptFiles)
        {
            var path = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(path))
            {
                path = item.ItemSpec;
            }

            try
            {
                using var reader = File.OpenText(path);
                loaded.Add((PromptYamlParser.Parse(reader, DeriveName(path)), path));
            }
            catch (PromptYamlException ex)
            {
                LogFileError(path, ex.Message);
                failed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFileError(path, $"Could not read prompt file: {ex.Message}");
                failed = true;
            }
        }

        var duplicates = loaded
            .GroupBy(entry => entry.Item1.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            // Left alone, these race: both publish, and whichever lands last wins the label.
            Log.LogError(
                $"Prompt name '{duplicate.Key}' is declared by {duplicate.Count()} files " +
                $"({string.Join(", ", duplicate.Select(entry => Path.GetFileName(entry.Item2)))}). Prompt names must be unique.");
            failed = true;
        }

        documents = loaded.Select(entry => (entry.Item1, entry.Item2)).ToList();
        return !failed;
    }

    /// <summary>The file's own <c>labels:</c> unioned with the build's, first-seen order preserved.</summary>
    private IReadOnlyList<string> ResolveLabels(PromptDocument document)
    {
        var resolved = new List<string>(document.Labels.Count + Labels.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var label in document.Labels.Concat(Labels))
        {
            if (!string.IsNullOrWhiteSpace(label) && seen.Add(label))
            {
                resolved.Add(label);
            }
        }

        return resolved;
    }

    private string? ResolveCommitHash()
    {
        if (!string.IsNullOrWhiteSpace(CommitHash))
        {
            return CommitHash!.Trim();
        }

        // ProjectFileOfTaskNode is the file the <UsingTask> executes from — for a NuGet consumer
        // that is the packaged .targets in the global package cache, never inside the user's
        // repository. The explicit ProjectDirectory from the .targets is the directory that is
        // actually being built; the engine fallback only serves callers wiring the task by hand.
        var projectDirectory = ProjectDirectory;
        if (string.IsNullOrEmpty(projectDirectory))
        {
            var projectFile = BuildEngine?.ProjectFileOfTaskNode;
            projectDirectory = string.IsNullOrEmpty(projectFile) ? null : Path.GetDirectoryName(projectFile);
        }

        return GitCommitResolver.Resolve(projectDirectory, EnvironmentReader);
    }

    /// <summary>Prompt name for files that omit <c>name:</c>: <c>Refund.prompt.yaml</c> becomes <c>Refund</c>.</summary>
    private static string DeriveName(string path)
    {
        var fileName = Path.GetFileName(path);
        var suffix = fileName.IndexOf(".prompt.", StringComparison.OrdinalIgnoreCase);
        return suffix > 0 ? fileName[..suffix] : Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>Logs against the file itself, so IDEs and CI annotations link straight to it.</summary>
    private void LogFileError(string file, string message) =>
        Log.LogError(subcategory: null, errorCode: null, helpKeyword: null, file: file, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0, message: message);
}
