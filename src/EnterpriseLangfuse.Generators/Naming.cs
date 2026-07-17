using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace EnterpriseLangfuse.Generators;

/// <summary>Maps Langfuse prompt and variable names onto valid, non-colliding C# identifiers.</summary>
internal static class Naming
{
    /// <summary>
    /// Identifiers the generated method body already uses. A prompt variable named <c>label</c> is
    /// perfectly legal in Langfuse, so parameters are uniquified against these rather than assumed
    /// not to clash.
    /// </summary>
    private static readonly string[] ReservedNames = ["provider", "label", "cancellationToken", "variables", "prompt"];

    /// <summary>
    /// Builds the method name for a prompt, e.g. <c>refund-agent</c> becomes <c>GetRefundAgentPromptAsync</c>.
    /// </summary>
    /// <returns>The method name, or null when the name contains nothing an identifier can be built from.</returns>
    public static string? MethodName(string promptName)
    {
        var pascal = ToPascalCase(promptName);
        return pascal.Length == 0 ? null : $"Get{pascal}PromptAsync";
    }

    /// <summary>
    /// Assigns each template variable a distinct camelCase parameter name.
    /// </summary>
    /// <remarks>
    /// The template name is kept alongside because it, not the parameter name, is the dictionary key
    /// the runtime renders against — sanitisation must never leak into the lookup.
    /// </remarks>
    public static EquatableArray<PromptVariable> Parameters(IReadOnlyList<string> variables)
    {
        var taken = new HashSet<string>(ReservedNames, StringComparer.Ordinal);
        var result = new PromptVariable[variables.Count];

        for (var i = 0; i < variables.Count; i++)
        {
            var candidate = ToCamelCase(variables[i]);
            if (candidate.Length == 0)
            {
                candidate = "value";
            }

            var unique = candidate;
            for (var suffix = 2; !taken.Add(unique); suffix++)
            {
                unique = candidate + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            result[i] = new PromptVariable(variables[i], Escape(unique));
        }

        return new EquatableArray<PromptVariable>(result);
    }

    /// <summary>Prefixes C# keywords with <c>@</c> so a variable named <c>class</c> still compiles.</summary>
    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None
        && SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
            ? identifier
            : "@" + identifier;

    private static string ToPascalCase(string value) => Convert(value, upperFirst: true);

    private static string ToCamelCase(string value) => Convert(value, upperFirst: false);

    /// <summary>
    /// Rewrites an arbitrary name as an identifier, treating every run of non-alphanumeric characters
    /// as a word boundary. Existing casing inside a word is preserved, so <c>customerName</c> survives
    /// intact rather than being flattened to <c>Customername</c>.
    /// </summary>
    private static string Convert(string value, bool upperFirst)
    {
        var builder = new StringBuilder(value.Length);
        var atBoundary = true;

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                atBoundary = true;
                continue;
            }

            // A digit cannot open an identifier; '_' can, and carries meaning, so it is kept.
            if (builder.Length == 0 && char.IsDigit(c))
            {
                continue;
            }

            if (atBoundary)
            {
                builder.Append(builder.Length == 0 && !upperFirst ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
                atBoundary = false;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
