// Shared source — must compile under netstandard2.0. See PromptDocument.cs.

using System;
using System.Collections.Generic;

namespace EnterpriseLangfuse.Shared;

/// <summary>One piece of a parsed template: either literal text or a variable placeholder.</summary>
internal readonly struct MustacheSegment
{
    private MustacheSegment(string value, bool isVariable)
    {
        Value = value;
        IsVariable = isVariable;
    }

    /// <summary>Literal text when <see cref="IsVariable"/> is false; the variable name when true.</summary>
    public string Value { get; }

    public bool IsVariable { get; }

    public static MustacheSegment Literal(string text) => new MustacheSegment(text, isVariable: false);

    public static MustacheSegment Variable(string name) => new MustacheSegment(name, isVariable: true);
}

/// <summary>
/// Parser for the <c>{{variable}}</c> subset of mustache that Langfuse prompts use.
/// </summary>
/// <remarks>
/// Deliberately not a full mustache engine. Langfuse prompt variables are plain substitutions, so
/// section/partial/comment tags (<c>{{#x}}</c>, <c>{{&gt;x}}</c>, <c>{{!x}}</c>, ...) are passed through as
/// literal text rather than interpreted. Anything this parser does not recognise as a variable is
/// preserved verbatim, so an unparseable tag degrades to literal output instead of throwing or
/// silently vanishing from a production prompt.
/// </remarks>
internal static class MustacheParser
{
    private const string Open = "{{";
    private const string Close = "}}";

    /// <summary>Sigils that mark a tag as a mustache construct we intentionally do not interpret.</summary>
    private static bool IsReservedSigil(char c) =>
        c == '#' || c == '/' || c == '^' || c == '!' || c == '>' || c == '&' || c == '{';

    /// <summary>
    /// Splits <paramref name="template"/> into literal and variable segments. Concatenating every
    /// segment's rendered form reproduces the original template exactly when each variable is
    /// replaced by its own name wrapped in braces.
    /// </summary>
    public static List<MustacheSegment> Parse(string? template)
    {
        var segments = new List<MustacheSegment>();
        if (string.IsNullOrEmpty(template))
        {
            return segments;
        }

        var source = template!;
        var literalStart = 0;
        var index = 0;

        while (index < source.Length)
        {
            var open = source.IndexOf(Open, index, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = source.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unterminated '{{' — everything from here on is literal.
                break;
            }

            var inner = source.Substring(open + Open.Length, close - open - Open.Length);
            var name = inner.Trim();

            if (!IsValidVariableName(name))
            {
                // Not a variable we handle: leave it in the literal run and keep scanning past it.
                index = close + Close.Length;
                continue;
            }

            if (open > literalStart)
            {
                segments.Add(MustacheSegment.Literal(source.Substring(literalStart, open - literalStart)));
            }

            segments.Add(MustacheSegment.Variable(name));
            index = close + Close.Length;
            literalStart = index;
        }

        if (literalStart < source.Length)
        {
            segments.Add(MustacheSegment.Literal(source.Substring(literalStart)));
        }

        return segments;
    }

    /// <summary>
    /// Appends every distinct variable in <paramref name="template"/> to <paramref name="into"/>,
    /// in first-seen order. <paramref name="seen"/> carries dedup state across multiple calls so a
    /// variable shared by several chat messages is reported once.
    /// </summary>
    public static void CollectVariables(string? template, List<string> into, HashSet<string> seen)
    {
        foreach (var segment in Parse(template))
        {
            if (segment.IsVariable && seen.Add(segment.Value))
            {
                into.Add(segment.Value);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a name we are willing to bind to a C# parameter:
    /// a non-empty identifier of letters, digits and underscores that does not start with a digit.
    /// </summary>
    public static bool IsValidVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var value = name!;
        if (IsReservedSigil(value[0]))
        {
            return false;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
