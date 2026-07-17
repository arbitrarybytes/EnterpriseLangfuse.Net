using EnterpriseLangfuse.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseLangfuse.Extensions.AI;

/// <summary>Adds Langfuse tracing to a <see cref="ChatClientBuilder"/> pipeline.</summary>
public static class ChatClientBuilderExtensions
{
    /// <summary>
    /// Traces every LLM call through this pipeline to Langfuse.
    /// </summary>
    /// <param name="builder">The pipeline being built.</param>
    /// <param name="configure">Configures capture behaviour.</param>
    /// <remarks>
    /// Place this <em>outermost</em> — i.e. call it last — if you want spans that include the time
    /// spent in inner middleware such as function invocation or retries. Placed innermost, it measures
    /// only the raw provider call, and tool-calling round trips appear as separate generations.
    /// <example>
    /// <code>
    /// services.AddChatClient(inner)
    ///         .UseFunctionInvocation()
    ///         .UseLangfuse();
    /// </code>
    /// </example>
    /// </remarks>
    public static ChatClientBuilder UseLangfuse(
        this ChatClientBuilder builder,
        Action<LangfuseChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((innerClient, services) =>
        {
            var options = new LangfuseChatClientOptions();
            configure?.Invoke(options);

            return new LangfuseChatClient(
                innerClient,
                services.GetRequiredService<ILangfuseTelemetry>(),
                options);
        });
    }
}
