using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core.Redis;

namespace RateLimiter.Core.Tests.Integration;

[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class RedisTokenBucketRateLimiterTests(RedisFixture redis)
{
    private RedisTokenBucketRateLimiter Create(FakeTimeProvider time, int capacity) =>
        new(redis.Connection, time, new RateLimiterOptions { PermitLimit = capacity, Window = TimeSpan.FromSeconds(60) });

    [Fact]
    public async Task AllowsFullBurst_ThenDeniesWithExactRetryAfter()
    {
        var limiter = Create(new FakeTimeProvider(DateTimeOffset.UtcNow), capacity: 10);
        var client = RedisFixture.UniqueClientId();

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 11; i++)
        {
            results.Add(await limiter.CheckAsync(client, "/orders"));
        }

        Assert.True(results.Take(10).All(r => r.IsAllowed));
        Assert.False(results[10].IsAllowed);
        // 10 tokens per 60s -> one token every 6s.
        Assert.Equal(TimeSpan.FromSeconds(6), results[10].RetryAfter);
    }

    [Fact]
    public async Task RefillsAtExpectedRate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = Create(time, capacity: 10);
        var client = RedisFixture.UniqueClientId();

        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync(client, "/orders");
        }

        time.Advance(TimeSpan.FromSeconds(30)); // exactly 5 tokens earned

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 6; i++)
        {
            results.Add(await limiter.CheckAsync(client, "/orders"));
        }

        Assert.True(results.Take(5).All(r => r.IsAllowed));
        Assert.False(results[5].IsAllowed);
    }

    [Fact]
    public async Task TokensDoNotAccumulateBeyondCapacity()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = Create(time, capacity: 5);
        var client = RedisFixture.UniqueClientId();

        for (var i = 0; i < 5; i++)
        {
            await limiter.CheckAsync(client, "/orders");
        }

        time.Advance(TimeSpan.FromHours(1));

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 6; i++)
        {
            results.Add(await limiter.CheckAsync(client, "/orders"));
        }

        Assert.True(results.Take(5).All(r => r.IsAllowed));
        Assert.False(results[5].IsAllowed);
    }

    [Fact]
    public async Task InstanceWithLaggingClock_CannotReEarnTokens()
    {
        // Two app instances whose clocks disagree by 5s. The lagging one must not
        // see "negative elapsed time" or roll last_refill backwards.
        var aheadClock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(5));
        var laggingClock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var aheadInstance = Create(aheadClock, capacity: 1);
        var laggingInstance = Create(laggingClock, capacity: 1);
        var client = RedisFixture.UniqueClientId();

        Assert.True((await aheadInstance.CheckAsync(client, "/orders")).IsAllowed);
        Assert.False((await laggingInstance.CheckAsync(client, "/orders")).IsAllowed);
    }
}
