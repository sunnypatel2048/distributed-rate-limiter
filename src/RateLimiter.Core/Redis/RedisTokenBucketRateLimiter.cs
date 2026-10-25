using System.Globalization;
using StackExchange.Redis;

namespace RateLimiter.Core.Redis;

/// <summary>
/// Distributed Token Bucket. Bucket state lives in a Redis hash
/// { tokens, last_refill_ms }; refill and consume happen in one atomic script.
/// </summary>
public sealed class RedisTokenBucketRateLimiter(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    RateLimiterOptions options) : IRateLimiter
{
    // Formatted once with the invariant culture: on a machine with a comma decimal
    // separator (e.g. de-DE), "0,000166" would reach Lua as nil and break refills.
    private readonly string _refillRatePerMs =
        (options.PermitLimit / options.Window.TotalMilliseconds).ToString("R", CultureInfo.InvariantCulture);

    // A bucket fully refills within one window, so an idle key can expire after that
    // and correctly read back as "full". 2x is just headroom.
    private readonly long _ttlMs = (long)options.Window.TotalMilliseconds * 2;

    public async Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            LuaScripts.TokenBucket,
            new RedisKey[] { RedisKeys.TokenBucket(clientId, route) },
            new RedisValue[] { options.PermitLimit, _refillRatePerMs, nowMs, _ttlMs }))!;

        var allowed = (long)result[0] == 1;

        if (allowed)
        {
            return RateLimitResult.Allowed(options.PermitLimit, (int)(long)result[1]);
        }

        return RateLimitResult.Denied(options.PermitLimit, TimeSpan.FromMilliseconds((long)result[2]));
    }
}
