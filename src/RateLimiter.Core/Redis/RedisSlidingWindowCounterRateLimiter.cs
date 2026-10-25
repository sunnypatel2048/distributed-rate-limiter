using StackExchange.Redis;

namespace RateLimiter.Core.Redis;

/// <summary>
/// Distributed Sliding Window Counter. The script reads the current and previous
/// window keys, computes the weighted estimate, and increments -- all atomically.
/// </summary>
public sealed class RedisSlidingWindowCounterRateLimiter(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    RateLimiterOptions options) : IRateLimiter
{
    private readonly long _windowMs = (long)options.Window.TotalMilliseconds;

    public async Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var currentWindowStartMs = nowMs / _windowMs * _windowMs;
        var elapsedMs = nowMs - currentWindowStartMs;

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            LuaScripts.SlidingWindowCounter,
            new RedisKey[]
            {
                RedisKeys.SlidingWindow(clientId, route, currentWindowStartMs),
                RedisKeys.SlidingWindow(clientId, route, currentWindowStartMs - _windowMs)
            },
            new RedisValue[] { options.PermitLimit, _windowMs, elapsedMs }))!;

        var allowed = (long)result[0] == 1;

        if (allowed)
        {
            // Redis truncates Lua numbers to integers in replies, so the script returns
            // the raw counts and the fractional estimate is recomputed here.
            var currentCount = (long)result[1];
            var previousCount = (long)result[2];
            var weight = Math.Clamp(1.0 - (elapsedMs / (double)_windowMs), 0.0, 1.0);
            var estimatedAfter = currentCount + (previousCount * weight);
            var remaining = (int)Math.Max(0, options.PermitLimit - estimatedAfter);
            return RateLimitResult.Allowed(options.PermitLimit, remaining);
        }

        // Conservative upper bound, same as the in-memory version.
        var retryAfterMs = currentWindowStartMs + _windowMs - nowMs;
        return RateLimitResult.Denied(options.PermitLimit, TimeSpan.FromMilliseconds(retryAfterMs));
    }
}
