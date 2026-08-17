using RateLimiter.Core.Algorithms;

namespace RateLimiter.Core;

public sealed class RateLimiterFactory(TimeProvider timeProvider) : IRateLimiterFactory
{
    public IRateLimiter Create(RateLimiterAlgorithm algorithm, RateLimiterOptions options) =>
        algorithm switch
        {
            RateLimiterAlgorithm.FixedWindow => new FixedWindowRateLimiter(timeProvider, options),
            RateLimiterAlgorithm.SlidingWindowCounter => new SlidingWindowCounterRateLimiter(timeProvider, options),
            RateLimiterAlgorithm.TokenBucket => new TokenBucketRateLimiter(timeProvider, options),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unrecognized rate limiter algorithm.")
        };
}