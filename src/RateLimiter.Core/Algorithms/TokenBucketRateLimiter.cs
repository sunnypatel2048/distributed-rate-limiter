using System.Collections.Concurrent;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// In-memory Token Bucket. Tokens refill continuously at a steady rate; each request
/// consumes one. Unlike the window algorithms, this tolerates short bursts up to
/// capacity while still enforcing a long-run average rate.
///
/// PermitLimit -> bucket capacity (max tokens / max burst size)
/// Window      -> time to refill from empty to full -> refillRate = PermitLimit / Window
/// </summary>
public sealed class TokenBucketRateLimiter(TimeProvider timeProvider, RateLimiterOptions options) : IRateLimiter
{
    // Floating-point comparisons on accumulated fractional tokens can land a hair
    // below an integer boundary (e.g. 0.9999999999999999 instead of 1.0) purely from
    // rounding. Without this tolerance, a request that should be allowed could be
    // incorrectly denied depending on how the elapsed-time math rounds.
    private const double Epsilon = 1e-9;

    private readonly ConcurrentDictionary<string, TokenBucketState> _store = new();
    private readonly double _refillRatePerSecond = options.PermitLimit / options.Window.TotalSeconds;

    private sealed record TokenBucketState(double Tokens, DateTimeOffset LastRefill);

    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        var key = $"{clientId}:{route}";
        var now = timeProvider.GetUtcNow();

        while (true)
        {
            // New clients start with a FULL bucket -- Token Bucket's whole value
            // proposition is tolerating an immediate burst, so starting empty would
            // defeat the point. (Starting empty is the more conservative alternative;
            // worth knowing as a tradeoff even though full is the standard default.)
            var oldState = _store.GetOrAdd(key, new TokenBucketState(options.PermitLimit, now));

            var elapsedSeconds = (now - oldState.LastRefill).TotalSeconds;
            var tokensEarned = elapsedSeconds * _refillRatePerSecond;
            var refilledTokens = Math.Min(options.PermitLimit, oldState.Tokens + tokensEarned);

            var allowed = refilledTokens + Epsilon >= 1.0;
            var newState = allowed
                ? new TokenBucketState(refilledTokens - 1.0, now)
                : new TokenBucketState(refilledTokens, now); // still bank the refill even when denied

            // Same CAS retry pattern as Monday, just applied to fractional state.
            if (_store.TryUpdate(key, newState, oldState))
            {
                if (allowed)
                {
                    var remaining = (int)Math.Floor(newState.Tokens);
                    return Task.FromResult(RateLimitResult.Allowed(options.PermitLimit, remaining));
                }

                // Exact time until one full token is available -- Token Bucket can give
                // a much tighter Retry-After than "wait until the window ends".
                var tokensNeeded = 1.0 - refilledTokens;
                var retryAfterSeconds = tokensNeeded / _refillRatePerSecond;
                return Task.FromResult(RateLimitResult.Denied(options.PermitLimit, TimeSpan.FromSeconds(retryAfterSeconds)));
            }
        }
    }
}
