using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core.Redis;

namespace RateLimiter.Core.Tests.Integration;

[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class RedisSlidingWindowCounterRateLimiterTests(RedisFixture redis)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private RedisSlidingWindowCounterRateLimiter Create(FakeTimeProvider time, int limit) =>
        new(redis.Connection, time, new RateLimiterOptions { PermitLimit = limit, Window = Window });

    private static FakeTimeProvider ClockAlignedToWindowStart()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)Window.TotalMilliseconds;
        return new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(nowMs / windowMs * windowMs));
    }

    [Fact]
    public async Task WeightedEstimate_MatchesHandCalculatedFormula()
    {
        var time = ClockAlignedToWindowStart();
        var limiter = Create(time, limit: 100);
        var client = RedisFixture.UniqueClientId();

        for (var i = 0; i < 100; i++)
        {
            await limiter.CheckAsync(client, "/orders");
        }

        // Next window, 25% elapsed -> weight 0.75.
        time.Advance(TimeSpan.FromSeconds(75));
        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync(client, "/orders");
        }

        // estimated = 10 + (100 * 0.75) = 85 -> allowed; afterwards 11 + 75 = 86 -> 14 left.
        var result = await limiter.CheckAsync(client, "/orders");
        Assert.True(result.IsAllowed);
        Assert.Equal(14, result.Remaining);
    }

    [Fact]
    public async Task PreviouslyDeniedRequest_CanBecomeAllowedAsWeightDecays()
    {
        var time = ClockAlignedToWindowStart();
        var limiter = Create(time, limit: 10);
        var client = RedisFixture.UniqueClientId();

        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync(client, "/orders");
        }

        time.Advance(TimeSpan.FromSeconds(60));
        var earlyInNewWindow = await limiter.CheckAsync(client, "/orders");

        time.Advance(TimeSpan.FromSeconds(55));
        var lateInNewWindow = await limiter.CheckAsync(client, "/orders");

        Assert.False(earlyInNewWindow.IsAllowed);
        Assert.True(lateInNewWindow.IsAllowed);
    }

    [Fact]
    public async Task IdleForMoreThanOneWindow_PreviousWindowCountsAsZero()
    {
        var time = ClockAlignedToWindowStart();
        var limiter = Create(time, limit: 1);
        var client = RedisFixture.UniqueClientId();

        await limiter.CheckAsync(client, "/orders");
        time.Advance(TimeSpan.FromSeconds(180));

        Assert.True((await limiter.CheckAsync(client, "/orders")).IsAllowed);
    }
}
