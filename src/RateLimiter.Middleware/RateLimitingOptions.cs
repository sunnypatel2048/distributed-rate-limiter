using RateLimiter.Core;

namespace RateLimiter.Middleware;

/// <summary>
/// Bound from the "RateLimiting" section of appsettings.json.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Header checked for an API key before falling back to the client IP.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>
    /// Route policies. The policy with the longest matching PathPrefix wins, so a
    /// policy with PathPrefix "/" acts as a catch-all default. Requests that match
    /// no policy are not rate limited.
    /// </summary>
    public List<RateLimitPolicy> Policies { get; set; } = [];
}

public sealed class RateLimitPolicy
{
    /// <summary>
    /// Unique name. Also used as the "route" part of the Redis key, so every path
    /// under one policy shares one quota per client (/api/orders and /api/orders/42
    /// draw from the same bucket).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Segment-aware, case-insensitive prefix: "/api/orders" matches "/api/orders/42" but not "/api/ordersX".</summary>
    public string PathPrefix { get; set; } = string.Empty;

    public RateLimiterAlgorithm Algorithm { get; set; } = RateLimiterAlgorithm.TokenBucket;

    public int PermitLimit { get; set; }

    /// <summary>Binds from "hh:mm:ss" in JSON, e.g. "00:01:00" for one minute.</summary>
    public TimeSpan Window { get; set; }

    /// <summary>What to do when the limiter itself fails (e.g. Redis is unreachable).</summary>
    public FailureMode FailureMode { get; set; } = FailureMode.FailOpen;
}

public enum FailureMode
{
    /// <summary>Let the request through and log a warning. Keeps the API up; protection is lost until the store recovers.</summary>
    FailOpen,

    /// <summary>Reject with 503. Protects fragile downstream systems; a store outage becomes an API outage for this route.</summary>
    FailClosed
}
