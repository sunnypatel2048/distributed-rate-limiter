using StackExchange.Redis;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// TODO (Mon): implement using a Lua script that INCRs a counter keyed by
/// ratelimit:{clientId}:{route}:{windowStart}, setting TTL = Window on first increment.
/// </summary>
public sealed class FixedWindowRateLimiter(IConnectionMultiplexer redis, RateLimiterOptions options) : IRateLimiter
{
    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Implement Fixed Window algorithm here.");
    }
}
