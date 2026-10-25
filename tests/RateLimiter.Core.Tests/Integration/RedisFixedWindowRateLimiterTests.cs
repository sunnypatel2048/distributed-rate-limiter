using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core.Redis;

namespace RateLimiter.Core.Tests.Integration;

[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class RedisFixedWindowRateLimiterTests(RedisFixture redis)
{
    private RedisFixedWindowRateLimiter Create(FakeTimeProvider time, int limit) =>
        new(redis.Connection, time, new RateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromSeconds(60) });

    [Fact]
    public async Task AllowsRequestsUpToLimit_ThenDeniesTheNext()
    {
        var limiter = Create(new FakeTimeProvider(DateTimeOffset.UtcNow), limit: 3);
        var client = RedisFixture.UniqueClientId();

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 4; i++)
        {
            results.Add(await limiter.CheckAsync(client, "/orders"));
        }

        Assert.True(results.Take(3).All(r => r.IsAllowed));
        Assert.Equal(new[] { 2, 1, 0 }, results.Take(3).Select(r => r.Remaining));
        Assert.False(results[3].IsAllowed);
        Assert.True(results[3].RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task ResetsAfterWindowRollsOver()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = Create(time, limit: 1);
        var client = RedisFixture.UniqueClientId();

        var first = await limiter.CheckAsync(client, "/orders");
        var secondSameWindow = await limiter.CheckAsync(client, "/orders");
        time.Advance(TimeSpan.FromSeconds(61));
        var afterRollover = await limiter.CheckAsync(client, "/orders");

        Assert.True(first.IsAllowed);
        Assert.False(secondSameWindow.IsAllowed);
        Assert.True(afterRollover.IsAllowed);
    }

    [Fact]
    public async Task IsolatesDifferentClientsAndRoutes()
    {
        var limiter = Create(new FakeTimeProvider(DateTimeOffset.UtcNow), limit: 1);
        var clientA = RedisFixture.UniqueClientId();
        var clientB = RedisFixture.UniqueClientId();

        Assert.True((await limiter.CheckAsync(clientA, "/orders")).IsAllowed);
        Assert.True((await limiter.CheckAsync(clientB, "/orders")).IsAllowed);
        Assert.True((await limiter.CheckAsync(clientA, "/products")).IsAllowed);
    }
}
