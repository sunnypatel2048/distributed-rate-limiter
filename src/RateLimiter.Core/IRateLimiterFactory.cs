namespace RateLimiter.Core;

/// <summary>
/// Creates IRateLimiter instances for a given algorithm + options combination.
///
/// A factory (rather than registering all three algorithms directly in DI) is
/// needed because each rate limiter needs its own RateLimiterOptions (different
/// limits per route) decided at runtime, not at startup. The factory itself does
/// NOT cache instances -- callers that need a limiter to persist across requests
/// (e.g. the middleware, wired up next week) are responsible for holding onto
/// what this returns rather than calling Create() fresh on every request.
/// </summary>
public interface IRateLimiterFactory
{
    IRateLimiter Create(RateLimiterAlgorithm algorithm, RateLimiterOptions options);
}