using StackExchange.Redis;

namespace RateLimiter.Core.Redis;

/// <summary>
/// Distributed Fixed Window. Same algorithm as the in-memory version, but the
/// check-and-increment runs as one Lua script inside Redis, so it's atomic across
/// every app instance -- no CAS retry loop needed.
/// </summary>
public sealed class RedisFixedWindowRateLimiter(
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

        // "Now" comes from TimeProvider, not Redis's clock, so FakeTimeProvider
        // still controls window rollover in tests.
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowStartMs = nowMs / _windowMs * _windowMs;

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            LuaScripts.FixedWindow,
            new RedisKey[] { RedisKeys.FixedWindow(clientId, route, windowStartMs) },
            new RedisValue[] { options.PermitLimit, _windowMs }))!;

        var allowed = (long)result[0] == 1;
        var count = (int)(long)result[1];

        if (allowed)
        {
            return RateLimitResult.Allowed(options.PermitLimit, options.PermitLimit - count);
        }

        var retryAfterMs = windowStartMs + _windowMs - nowMs;
        return RateLimitResult.Denied(options.PermitLimit, TimeSpan.FromMilliseconds(retryAfterMs));
    }
}
