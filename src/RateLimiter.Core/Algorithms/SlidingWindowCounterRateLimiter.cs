using StackExchange.Redis;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// TODO (Mon): implement by weighting the previous and current fixed windows
/// by the fraction of the current window elapsed.
/// </summary>
public sealed class SlidingWindowCounterRateLimiter(IConnectionMultiplexer redis, RateLimiterOptions options) : IRateLimiter
{
    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Implement Sliding Window Counter algorithm here.");
    }
}
