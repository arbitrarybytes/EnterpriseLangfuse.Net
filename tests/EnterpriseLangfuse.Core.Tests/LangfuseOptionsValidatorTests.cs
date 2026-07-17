using Microsoft.Extensions.Options;
using Shouldly;

namespace EnterpriseLangfuse.Core.Tests;

public static class LangfuseOptionsValidatorTests
{
    [Fact]
    public static void Accepts_a_valid_configuration()
    {
        Validate(_ => { }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public static void Rejects_blank_credentials(string blank)
    {
        Validate(o => o.PublicKey = blank).ShouldFailWith(f => f.Contains("PublicKey is required"));
        Validate(o => o.SecretKey = blank).ShouldFailWith(f => f.Contains("SecretKey is required"));
    }

    [Fact]
    public static void Reports_every_problem_at_once_rather_than_one_per_restart()
    {
        var result = Validate(o =>
        {
            o.PublicKey = string.Empty;
            o.SecretKey = string.Empty;
            o.RequestTimeout = TimeSpan.Zero;
        });

        result.Failures.ShouldNotBeNull().Count().ShouldBe(3);
    }

    [Fact]
    public static void Rejects_a_relative_base_url()
    {
        // A relative base address silently produces malformed request URIs at the first call.
        Validate(o => o.BaseUrl = new Uri("/langfuse", UriKind.Relative))
            .ShouldFailWith(f => f.Contains("absolute URI"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public static void Rejects_a_non_positive_queue_capacity(int capacity)
    {
        Validate(o =>
        {
            o.TelemetryQueueCapacity = capacity;
            o.TelemetryBatchSize = 1;
        }).ShouldFailWith(f => f.Contains("TelemetryQueueCapacity must be at least 1"));
    }

    [Fact]
    public static void Rejects_a_non_positive_batch_size()
    {
        Validate(o => o.TelemetryBatchSize = 0)
            .ShouldFailWith(f => f.Contains("TelemetryBatchSize must be at least 1"));
    }

    [Fact]
    public static void Rejects_a_batch_that_can_never_be_filled()
    {
        Validate(o =>
        {
            o.TelemetryQueueCapacity = 5;
            o.TelemetryBatchSize = 50;
        }).ShouldFailWith(f => f.Contains("can never be filled"));
    }

    [Fact]
    public static void Rejects_non_positive_intervals_and_timeouts()
    {
        Validate(o => o.TelemetryFlushInterval = TimeSpan.Zero)
            .ShouldFailWith(f => f.Contains("TelemetryFlushInterval must be positive"));

        Validate(o => o.RequestTimeout = TimeSpan.FromSeconds(-1))
            .ShouldFailWith(f => f.Contains("RequestTimeout must be positive"));
    }

    [Fact]
    public static void Rejects_negative_durations()
    {
        var negative = TimeSpan.FromSeconds(-1);

        Validate(o => o.PromptCacheDuration = negative)
            .ShouldFailWith(f => f.Contains("PromptCacheDuration cannot be negative"));

        Validate(o => o.FallbackCacheDuration = negative)
            .ShouldFailWith(f => f.Contains("FallbackCacheDuration cannot be negative"));

        Validate(o => o.ShutdownDrainTimeout = negative)
            .ShouldFailWith(f => f.Contains("ShutdownDrainTimeout cannot be negative"));
    }

    [Fact]
    public static void Allows_a_zero_cache_duration_to_mean_do_not_cache()
    {
        Validate(o => o.PromptCacheDuration = TimeSpan.Zero).Succeeded.ShouldBeTrue();
    }

    /// <summary>Asserts a specific failure was reported, keeping the nullability noise out of tests.</summary>
    private static void ShouldFailWith(this ValidateOptionsResult result, Func<string, bool> predicate)
    {
        result.Succeeded.ShouldBeFalse();
        result.Failures.ShouldNotBeNull().ShouldContain(f => predicate(f));
    }

    private static ValidateOptionsResult Validate(Action<LangfuseOptions> configure)
    {
        var options = new LangfuseOptions { PublicKey = "pk-lf-x", SecretKey = "sk-lf-x" };
        configure(options);

        return new LangfuseOptionsValidator().Validate(name: null, options);
    }
}
