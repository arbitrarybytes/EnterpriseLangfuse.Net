namespace EnterpriseLangfuse.Generators;

/// <summary>One <c>{{mustache}}</c> variable and the C# parameter it is bound to.</summary>
/// <param name="TemplateName">The variable's name as written in the template — the dictionary key.</param>
/// <param name="ParameterName">The generated parameter's name, already a valid, unique identifier.</param>
internal sealed record PromptVariable(string TemplateName, string ParameterName);

/// <summary>
/// A diagnostic captured during parsing, in a form that can live inside a cached model.
/// </summary>
/// <remarks>
/// Roslyn's <c>Diagnostic</c> and <c>Location</c> are deliberately kept out of the pipeline: they
/// hold references to syntax and source-text objects that compare by reference and pin memory
/// between compilations. Carrying the identifying data instead lets the model stay cacheable, and
/// the real <c>Diagnostic</c> is materialised in the source-output stage where it costs nothing.
/// </remarks>
/// <param name="Descriptor">Which rule fired.</param>
/// <param name="FilePath">The <c>.prompt.yaml</c> file the diagnostic points at.</param>
/// <param name="Message">The message argument formatted into the descriptor.</param>
internal sealed record PromptDiagnostic(PromptDiagnosticKind Descriptor, string FilePath, string Message);

/// <summary>Identifies which <see cref="PromptDiagnostics"/> descriptor a <see cref="PromptDiagnostic"/> refers to.</summary>
internal enum PromptDiagnosticKind
{
    /// <summary>ELF001 — the file does not satisfy the prompt schema.</summary>
    InvalidPromptYaml,

    /// <summary>ELF002 — two files map to the same generated method.</summary>
    DuplicatePromptName,
}

/// <summary>
/// Everything the emitter needs about one <c>.prompt.yaml</c> file, and nothing else.
/// </summary>
/// <remarks>
/// This is the pipeline's cache key. It holds only strings and structurally comparable values, so an
/// edit to a prompt file that does not change its name or variables — a wording change, say —
/// produces an equal model and Roslyn skips re-running the emitter for the whole compilation.
/// </remarks>
/// <param name="FilePath">Source file, used to locate diagnostics and to order duplicate resolution.</param>
/// <param name="PromptName">The prompt's name in Langfuse; passed verbatim to <c>GetPromptAsync</c>.</param>
/// <param name="MethodName">The full generated method name, e.g. <c>GetRefundAgentPromptAsync</c>.</param>
/// <param name="Variables">The template's variables, in first-seen order.</param>
/// <param name="Error">Non-null when the file could not be parsed; <see cref="MethodName"/> is then meaningless.</param>
internal sealed record PromptModel(
    string FilePath,
    string PromptName,
    string MethodName,
    EquatableArray<PromptVariable> Variables,
    PromptDiagnostic? Error);
