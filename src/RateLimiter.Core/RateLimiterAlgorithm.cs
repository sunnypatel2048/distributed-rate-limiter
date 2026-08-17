namespace RateLimiter.Core;

public enum RateLimiterAlgorithm
{
    FixedWindow,
    SlidingWindowCounter,
    TokenBucket
}