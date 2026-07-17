using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RefundAgent;

/// <summary>
/// A stand-in <see cref="IChatClient"/> so the sample runs without an LLM API key.
/// </summary>
/// <remarks>
/// Returns a canned answer with plausible usage and a small delay, because the point of the sample is
/// the surrounding machinery — the prompt pipeline and the tracing — not the model. Swap this for a
/// real provider (<c>new AnthropicChatClient(apiKey)</c>, <c>OpenAIClient</c>, Ollama, …) and nothing
/// else in Program.cs changes: that substitutability is what <c>IChatClient</c> buys you, and it is
/// why <c>UseLangfuse()</c> traces any provider without knowing which one it wrapped.
/// </remarks>
internal sealed class CannedChatClient : IChatClient
{
    private const string Answer =
        "I'm sorry your order arrived damaged. Order A-4815 is within the 30-day window, " +
        "so I've approved a full refund. It should appear on your original payment method " +
        "within 5 business days.";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // A little latency so the traced duration in Langfuse is not zero.
        await Task.Delay(120, cancellationToken);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer))
        {
            ModelId = options?.ModelId ?? "canned-model",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = EstimateTokens(messages),
                OutputTokenCount = EstimateTokens([new ChatMessage(ChatRole.Assistant, Answer)]),
                TotalTokenCount = EstimateTokens(messages) + EstimateTokens([new ChatMessage(ChatRole.Assistant, Answer)]),
            },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in Answer.Split(' '))
        {
            await Task.Delay(15, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>Rough token estimate (~4 chars/token) so the traced usage looks realistic.</summary>
    private static long EstimateTokens(IEnumerable<ChatMessage> messages) =>
        Math.Max(1, messages.Sum(m => m.Text?.Length ?? 0) / 4);
}
