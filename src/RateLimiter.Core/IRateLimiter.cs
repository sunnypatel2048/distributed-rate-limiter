namespace RateLimiter.Core;

/// <summary>
/// Strategy interface implemented by each rate limiting algorithm
/// (Fixed Window, Sliding Window Counter, Token Bucket).
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Checks whether a request from the given client, on the given route,
    /// is allowed under the current limit. Implementations must perform
    /// the check-and-update as a single atomic operation against Redis.
    /// </summary>
    Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default);
}
