namespace RateLimiter.Core;

/// <summary>
/// Outcome of a rate limit check, enough to populate response headers.
/// </summary>
public sealed record RateLimitResult(
    bool IsAllowed,
    int Limit,
    int Remaining,
    TimeSpan RetryAfter)
{
    public static RateLimitResult Allowed(int limit, int remaining) =>
        new(true, limit, remaining, TimeSpan.Zero);

    public static RateLimitResult Denied(int limit, TimeSpan retryAfter) =>
        new(false, limit, 0, retryAfter);
}
