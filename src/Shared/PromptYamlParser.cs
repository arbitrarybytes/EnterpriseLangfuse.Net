// Shared source — must compile under netstandard2.0. See PromptDocument.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YamlDotNet.RepresentationModel;

namespace EnterpriseLangfuse.Shared;

/// <summary>Raised when a <c>.prompt.yaml</c> file does not satisfy the prompt schema.</summary>
internal sealed class PromptYamlException : Exception
{
    public PromptYamlException(string message)
        : base(message)
    {
    }

    public PromptYamlException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Parses <c>.prompt.yaml</c> files into <see cref="PromptDocument"/>.
/// </summary>
/// <remarks>
/// Uses YamlDotNet's <see cref="YamlStream"/> representation model rather than its reflection-based
/// <c>Deserializer</c>. That is a deliberate constraint, not a stylistic one: this parser runs inside
/// the L3 offline fallback at runtime, and the library is shipped <c>IsAotCompatible</c>. Reflection
/// deserialization would produce trim/AOT warnings and could fail once trimmed; the DOM API is
/// reflection-free and safe under Native AOT.
/// </remarks>
internal static class PromptYamlParser
{
    /// <param name="yaml">Raw YAML text.</param>
    /// <param name="fallbackName">
    /// Prompt name to use when the document omits <c>name:</c> — normally derived from the file name.
    /// </param>
    public static PromptDocument Parse(string yaml, string? fallbackName = null)
    {
        using var reader = new StringReader(yaml ?? throw new ArgumentNullException(nameof(yaml)));
        return Parse(reader, fallbackName);
    }

    public static PromptDocument Parse(TextReader reader, string? fallbackName = null)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(reader);
        }
        catch (Exception ex) when (ex is not PromptYamlException)
        {
            throw new PromptYamlException($"Prompt YAML is not well-formed: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            throw new PromptYamlException("Prompt YAML is empty.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new PromptYamlException("Prompt YAML root must be a mapping (a set of 'key: value' entries).");
        }

        var document = new PromptDocument
        {
            Name = GetScalar(root, "name") ?? fallbackName ?? string.Empty,
            CommitMessage = GetScalar(root, "commitMessage"),
        };

        if (document.Name.Length == 0)
        {
            throw new PromptYamlException("Prompt YAML must declare 'name', or be loaded from a named file.");
        }

        ReadStringList(root, "labels", document.Labels);
        ReadStringList(root, "tags", document.Tags);
        ReadConfig(root, document.Config);
        ReadBody(root, document);

        return document;
    }

    /// <summary>
    /// Resolves the prompt body and kind. An explicit <c>type:</c> wins; otherwise the kind is
    /// inferred from which body key is present, so simple files need no ceremony.
    /// </summary>
    private static void ReadBody(YamlMappingNode root, PromptDocument document)
    {
        var declaredType = GetScalar(root, "type");
        var hasMessages = TryGet(root, "messages", out var messagesNode);
        var text = GetScalar(root, "prompt") ?? GetScalar(root, "text");

        PromptKind kind;
        if (declaredType is null)
        {
            kind = hasMessages ? PromptKind.Chat : PromptKind.Text;
        }
        else if (string.Equals(declaredType, "chat", StringComparison.OrdinalIgnoreCase))
        {
            kind = PromptKind.Chat;
        }
        else if (string.Equals(declaredType, "text", StringComparison.OrdinalIgnoreCase))
        {
            kind = PromptKind.Text;
        }
        else
        {
            throw new PromptYamlException($"Prompt '{document.Name}' has unknown type '{declaredType}'. Expected 'text' or 'chat'.");
        }

        document.Kind = kind;

        if (kind == PromptKind.Chat)
        {
            if (!hasMessages || messagesNode is not YamlSequenceNode messages)
            {
                throw new PromptYamlException($"Chat prompt '{document.Name}' must declare a 'messages' sequence.");
            }

            foreach (var entry in messages)
            {
                if (entry is not YamlMappingNode message)
                {
                    throw new PromptYamlException($"Chat prompt '{document.Name}' has a message that is not a mapping.");
                }

                var role = GetScalar(message, "role")
                    ?? throw new PromptYamlException($"Chat prompt '{document.Name}' has a message without a 'role'.");
                var content = GetScalar(message, "content")
                    ?? throw new PromptYamlException($"Chat prompt '{document.Name}' message '{role}' has no 'content'.");

                document.Messages.Add(new PromptMessageDocument(role, content));
            }

            if (document.Messages.Count == 0)
            {
                throw new PromptYamlException($"Chat prompt '{document.Name}' must declare at least one message.");
            }

            return;
        }

        document.Text = text
            ?? throw new PromptYamlException($"Text prompt '{document.Name}' must declare a 'prompt' body.");
    }

    /// <summary>
    /// Ceiling on nodes materialised from <c>config:</c>. YAML aliases are re-expanded at every
    /// reference, so an anchored-doubling document ("billion laughs") a few lines long can otherwise
    /// materialise exponentially many nodes — and this parser runs inside the Roslyn generator, where
    /// that means hanging the consumer's IDE. Real model configs are tens of entries; the ceiling is
    /// orders of magnitude above any legitimate file.
    /// </summary>
    private const int MaxConfigNodes = 10_000;

    /// <summary>Depth ceiling for <c>config:</c>; a cyclic alias graph would otherwise recurse forever.</summary>
    private const int MaxConfigDepth = 32;

    private static void ReadConfig(YamlMappingNode root, Dictionary<string, object?> config)
    {
        if (!TryGet(root, "config", out var node))
        {
            return;
        }

        if (node is not YamlMappingNode mapping)
        {
            throw new PromptYamlException("Prompt 'config' must be a mapping.");
        }

        var budget = MaxConfigNodes;
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode key && key.Value is { } name)
            {
                config[name] = ToClrValue(pair.Value, ref budget, depth: 0);
            }
        }
    }

    private static void ReadStringList(YamlMappingNode root, string key, List<string> into)
    {
        if (!TryGet(root, key, out var node))
        {
            return;
        }

        switch (node)
        {
            // A single scalar is accepted as a one-element list: `labels: production`.
            case YamlScalarNode scalar when scalar.Value is { Length: > 0 } single:
                into.Add(single);
                break;

            case YamlSequenceNode sequence:
                foreach (var item in sequence)
                {
                    if (item is not YamlScalarNode { Value: { } value })
                    {
                        throw new PromptYamlException($"Prompt '{key}' must contain only scalar values.");
                    }

                    into.Add(value);
                }

                break;

            default:
                throw new PromptYamlException($"Prompt '{key}' must be a scalar or a sequence of scalars.");
        }
    }

    /// <summary>Projects an arbitrary YAML node onto plain CLR values for pass-through config.</summary>
    private static object? ToClrValue(YamlNode node, ref int budget, int depth)
    {
        if (--budget < 0)
        {
            throw new PromptYamlException(
                $"Prompt 'config' expands to more than {MaxConfigNodes} values — most likely a YAML alias bomb.");
        }

        if (depth > MaxConfigDepth)
        {
            throw new PromptYamlException(
                $"Prompt 'config' nests deeper than {MaxConfigDepth} levels — most likely a cyclic YAML alias.");
        }

        switch (node)
        {
            case YamlScalarNode scalar:
                return ToScalarValue(scalar);

            case YamlSequenceNode sequence:
            {
                var list = new List<object?>(sequence.Children.Count);
                foreach (var child in sequence)
                {
                    list.Add(ToClrValue(child, ref budget, depth + 1));
                }

                return list;
            }

            case YamlMappingNode mapping:
            {
                var map = new Dictionary<string, object?>();
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is YamlScalarNode { Value: { } key })
                    {
                        map[key] = ToClrValue(pair.Value, ref budget, depth + 1);
                    }
                }

                return map;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Applies YAML 1.1 core scalar resolution. Quoted scalars stay strings so that
    /// <c>version: "1.0"</c> does not silently become a double.
    /// </summary>
    private static object? ToScalarValue(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
        {
            return null;
        }

        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
        {
            return value;
        }

        if (value.Length == 0 || value is "null" or "~" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return true;
        }

        if (value is "false" or "False" or "FALSE")
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return value;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        TryGet(mapping, key, out var node) && node is YamlScalarNode scalar ? scalar.Value : null;
}
