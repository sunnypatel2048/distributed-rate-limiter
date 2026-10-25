using RateLimiter.Core.Redis;
using StackExchange.Redis;

namespace RateLimiter.Core;

/// <summary>
/// Creates Redis-backed (distributed) rate limiters. Same IRateLimiterFactory contract
/// as the in-memory RateLimiterFactory, so the storage backend is chosen with a single
/// DI registration and nothing downstream changes.
///
/// Unlike the in-memory limiters, these objects hold no state of their own -- it all
/// lives in Redis -- so creating a fresh instance per request is safe here.
/// </summary>
public sealed class RedisRateLimiterFactory(IConnectionMultiplexer redis, TimeProvider timeProvider) : IRateLimiterFactory
{
    public IRateLimiter Create(RateLimiterAlgorithm algorithm, RateLimiterOptions options) =>
        algorithm switch
        {
            RateLimiterAlgorithm.FixedWindow => new RedisFixedWindowRateLimiter(redis, timeProvider, options),
            RateLimiterAlgorithm.SlidingWindowCounter => new RedisSlidingWindowCounterRateLimiter(redis, timeProvider, options),
            RateLimiterAlgorithm.TokenBucket => new RedisTokenBucketRateLimiter(redis, timeProvider, options),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unrecognized rate limiter algorithm.")
        };
}
