using System.Diagnostics.CodeAnalysis;
using EnterpriseLangfuse.Prompts;
using Microsoft.Extensions.AI;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>
/// Links an LLM call to the prompt revision that produced it.
/// </summary>
/// <remarks>
/// Langfuse can only attribute quality, cost and latency to a specific prompt version if each
/// generation carries that version. Nothing in MEAI's <see cref="ChatOptions"/> models a prompt, so
/// the link is carried in <see cref="ChatOptions.AdditionalProperties"/> — the extension point MEAI
/// provides for exactly this — and read back by <see cref="LangfuseChatClient"/>.
/// <para>
/// This travels on <see cref="ChatOptions"/> rather than an <c>AsyncLocal</c> ambient context on
/// purpose: an ambient value would leak across concurrent calls that share an execution context and
/// silently attribute a generation to the wrong prompt.
/// </para>
/// </remarks>
public static class LangfusePromptContext
{
    internal const string PropertyKey = "enterpriselangfuse.prompt";

    /// <summary>
    /// Attaches the prompt that produced this call, so the generation is linked to its revision.
    /// </summary>
    /// <param name="options">The options for the call.</param>
    /// <param name="prompt">The compiled prompt being sent.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <example>
    /// <code>
    /// var prompt = await provider.GetRefundAgentPromptAsync(customerName: "Ada");
    /// var options = new ChatOptions { ModelId = "claude-opus-4-8" }.WithLangfusePrompt(prompt);
    /// var response = await chatClient.GetResponseAsync(prompt.ToChatMessages(), options);
    /// </code>
    /// </example>
    public static ChatOptions WithLangfusePrompt(this ChatOptions options, CompiledPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompt);

        options.AdditionalProperties ??= [];
        options.AdditionalProperties[PropertyKey] = prompt;

        return options;
    }

    /// <summary>
    /// Returns options safe to hand to the inner client: the prompt entry is removed on a clone.
    /// </summary>
    /// <remarks>
    /// Providers are free to serialise <see cref="ChatOptions.AdditionalProperties"/> into their
    /// request body; forwarding a live <see cref="CompiledPrompt"/> there would at best leak prompt
    /// metadata to the model provider and at worst fail their serialiser. The caller's instance is
    /// never mutated — they may reuse it for the next call.
    /// </remarks>
    internal static ChatOptions? StripForDelegation(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null || !options.AdditionalProperties.ContainsKey(PropertyKey))
        {
            return options;
        }

        var forwarded = options.Clone();
        forwarded.AdditionalProperties!.Remove(PropertyKey);
        return forwarded;
    }

    /// <summary>Reads back a prompt attached by <see cref="WithLangfusePrompt"/>.</summary>
    internal static bool TryGet(ChatOptions options, [NotNullWhen(true)] out CompiledPrompt? prompt)
    {
        prompt = null;

        if (options.AdditionalProperties is null)
        {
            return false;
        }

        if (options.AdditionalProperties.TryGetValue(PropertyKey, out var value) && value is CompiledPrompt compiled)
        {
            prompt = compiled;
            return true;
        }

        return false;
    }
}
