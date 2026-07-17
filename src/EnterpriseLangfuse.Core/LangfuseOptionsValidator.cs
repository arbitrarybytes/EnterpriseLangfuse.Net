using Microsoft.Extensions.Options;

namespace EnterpriseLangfuse;

/// <summary>
/// Validates <see cref="LangfuseOptions"/> at startup.
/// </summary>
/// <remarks>
/// Written by hand rather than using <c>ValidateDataAnnotations</c>, which walks attributes
/// reflectively and is annotated <c>RequiresUnreferencedCode</c>: it produces an IL2026 trim warning
/// and can silently stop validating once trimmed. Since this library ships
/// <c>IsAotCompatible</c>, the validation path has to be reflection-free — and being explicit lets
/// each message name the remedy rather than reporting "the Field is required".
/// </remarks>
internal sealed class LangfuseOptionsValidator : IValidateOptions<LangfuseOptions>
{
    public ValidateOptionsResult Validate(string? name, LangfuseOptions options)
    {
        List<string>? failures = null;

        if (string.IsNullOrWhiteSpace(options.PublicKey))
        {
            Add(ref failures, "PublicKey is required. Find it in Langfuse under Settings > API Keys (it starts with 'pk-lf-').");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            Add(ref failures, "SecretKey is required. Find it in Langfuse under Settings > API Keys (it starts with 'sk-lf-').");
        }

        // A relative base address silently produces malformed request URIs at the first call rather
        // than at startup, so it is worth catching here.
        if (!options.BaseUrl.IsAbsoluteUri)
        {
            Add(ref failures, $"BaseUrl must be an absolute URI, but was '{options.BaseUrl}'.");
        }

        if (options.TelemetryQueueCapacity < 1)
        {
            Add(ref failures, $"TelemetryQueueCapacity must be at least 1, but was {options.TelemetryQueueCapacity}.");
        }

        if (options.TelemetryBatchSize < 1)
        {
            Add(ref failures, $"TelemetryBatchSize must be at least 1, but was {options.TelemetryBatchSize}.");
        }

        // A batch larger than the queue can never be filled, so the dispatcher would always wait out
        // the flush interval before sending — a subtle latency bug rather than a loud failure.
        if (options.TelemetryBatchSize > options.TelemetryQueueCapacity)
        {
            Add(
                ref failures,
                $"TelemetryBatchSize ({options.TelemetryBatchSize}) cannot exceed TelemetryQueueCapacity " +
                $"({options.TelemetryQueueCapacity}); a batch that large can never be filled.");
        }

        if (options.TelemetryFlushInterval <= TimeSpan.Zero)
        {
            Add(ref failures, $"TelemetryFlushInterval must be positive, but was {options.TelemetryFlushInterval}.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            Add(ref failures, $"RequestTimeout must be positive, but was {options.RequestTimeout}.");
        }

        if (options.PromptCacheDuration < TimeSpan.Zero)
        {
            Add(ref failures, $"PromptCacheDuration cannot be negative, but was {options.PromptCacheDuration}.");
        }

        if (options.FallbackCacheDuration < TimeSpan.Zero)
        {
            Add(ref failures, $"FallbackCacheDuration cannot be negative, but was {options.FallbackCacheDuration}.");
        }

        if (options.ShutdownDrainTimeout < TimeSpan.Zero)
        {
            Add(ref failures, $"ShutdownDrainTimeout cannot be negative, but was {options.ShutdownDrainTimeout}.");
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Add(ref List<string>? failures, string message) => (failures ??= []).Add(message);
}
