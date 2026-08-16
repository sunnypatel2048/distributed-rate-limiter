using StackExchange.Redis;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// TODO (Tue): implement using a Lua script storing { tokens, lastRefillTimestamp }
/// in a Redis hash, refilling proportionally to elapsed time on each check.
/// </summary>
public sealed class TokenBucketRateLimiter(IConnectionMultiplexer redis, RateLimiterOptions options) : IRateLimiter
{
    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Implement Token Bucket algorithm here.");
    }
}
