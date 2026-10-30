using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RateLimiter.Middleware;

/// <summary>
/// Rate limiter metrics, built on System.Diagnostics.Metrics -- the standard .NET
/// metrics API. Anything that listens to .NET meters (OpenTelemetry, dotnet-counters,
/// a test's MeterListener) can read them without this library knowing which.
///
/// Tags are deliberately low-cardinality: policy, algorithm and outcome. The client id
/// is NOT a tag -- there can be millions of clients, and every distinct tag value
/// becomes a separate time series in the metrics backend.
/// </summary>
public sealed class RateLimiterMetrics
{
    public const string MeterName = "RateLimiter";

    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _checkDuration;

    public RateLimiterMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _decisions = meter.CreateCounter<long>(
            "ratelimiter.decisions",
            description: "Requests evaluated by the rate limiter, by policy, algorithm and outcome.");

        _checkDuration = meter.CreateHistogram<double>(
            "ratelimiter.check.duration",
            unit: "s",
            description: "Time spent in the rate limiter check (the Redis round trip for the distributed limiters).");
    }

    public void RecordDecision(RateLimitPolicy policy, RateLimitOutcome outcome, TimeSpan checkDuration)
    {
        var tags = new TagList
        {
            { "policy", policy.Name },
            { "algorithm", policy.Algorithm.ToString() },
            { "outcome", outcome.ToTag() }
        };

        _decisions.Add(1, tags);
        _checkDuration.Record(checkDuration.TotalSeconds, tags);
    }
}

public enum RateLimitOutcome
{
    Allowed,
    Rejected,
    FailOpen,
    FailClosed
}

internal static class RateLimitOutcomeExtensions
{
    public static string ToTag(this RateLimitOutcome outcome) => outcome switch
    {
        RateLimitOutcome.Allowed => "allowed",
        RateLimitOutcome.Rejected => "rejected",
        RateLimitOutcome.FailOpen => "fail_open",
        RateLimitOutcome.FailClosed => "fail_closed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
