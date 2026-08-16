namespace RateLimiter.Core;

/// <summary>
/// Per-route/per-client configuration. One instance of this is passed
/// to whichever IRateLimiter algorithm is active.
/// </summary>
public sealed class RateLimiterOptions
{
    /// <summary>Max requests allowed within the window.</summary>
    public required int PermitLimit { get; init; }

    /// <summary>Size of the time window (or bucket refill period for Token Bucket).</summary>
    public required TimeSpan Window { get; init; }
}
