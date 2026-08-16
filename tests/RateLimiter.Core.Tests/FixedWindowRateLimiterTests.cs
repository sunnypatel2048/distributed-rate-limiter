using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Core.Algorithms;
using Xunit;

namespace RateLimiter.Core.Tests;

public class FixedWindowRateLimiterTests
{
    private static RateLimiterOptions Options(int limit, TimeSpan window) =>
        new() { PermitLimit = limit, Window = window };

    [Fact]
    public async Task AllowsRequestsUpToLimit_ThenDeniesTheNext()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new FixedWindowRateLimiter(time, Options(limit: 3, window: TimeSpan.FromSeconds(60)));

        var r1 = await limiter.CheckAsync("client-a", "/orders");
        var r2 = await limiter.CheckAsync("client-a", "/orders");
        var r3 = await limiter.CheckAsync("client-a", "/orders");
        var r4 = await limiter.CheckAsync("client-a", "/orders");

        Assert.True(r1.IsAllowed);
        Assert.True(r2.IsAllowed);
        Assert.True(r3.IsAllowed);
        Assert.False(r4.IsAllowed);
        Assert.Equal(0, r4.Remaining);
    }

    [Fact]
    public async Task ResetsAfterWindowRollsOver()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new FixedWindowRateLimiter(time, Options(limit: 1, window: TimeSpan.FromSeconds(60)));

        var first = await limiter.CheckAsync("client-a", "/orders");
        var secondSameWindow = await limiter.CheckAsync("client-a", "/orders");

        time.Advance(TimeSpan.FromSeconds(61));
        var afterRollover = await limiter.CheckAsync("client-a", "/orders");

        Assert.True(first.IsAllowed);
        Assert.False(secondSameWindow.IsAllowed);
        Assert.True(afterRollover.IsAllowed);
    }

    [Fact]
    public async Task IsolatesDifferentClientsAndRoutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new FixedWindowRateLimiter(time, Options(limit: 1, window: TimeSpan.FromSeconds(60)));

        var clientA = await limiter.CheckAsync("client-a", "/orders");
        var clientB = await limiter.CheckAsync("client-b", "/orders");
        var differentRoute = await limiter.CheckAsync("client-a", "/products");

        Assert.True(clientA.IsAllowed);
        Assert.True(clientB.IsAllowed);
        Assert.True(differentRoute.IsAllowed);
    }

    [Fact]
    public async Task DemonstratesBoundaryBurst_KnownFixedWindowTradeoff()
    {
        // This isn't a bug — it's documenting the exact weakness the README's
        // algorithm comparison table calls out: up to 2x the limit can slip
        // through right at a window boundary.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new FixedWindowRateLimiter(time, Options(limit: 5, window: TimeSpan.FromSeconds(60)));

        for (var i = 0; i < 5; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        time.Advance(TimeSpan.FromSeconds(60));

        var firstOfNewWindow = await limiter.CheckAsync("client-a", "/orders");

        Assert.True(firstOfNewWindow.IsAllowed);
    }

    [Fact]
    public async Task ConcurrentRequests_OnlyAllowsExactlyTheLimit()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        const int limit = 20;
        var limiter = new FixedWindowRateLimiter(time, Options(limit, TimeSpan.FromSeconds(60)));

        // Task.Run forces real thread-pool parallelism — without it, these would
        // run sequentially on one thread and never actually exercise the CAS loop.
        var tasks = Enumerable.Range(0, limit * 3)
            .Select(_ => Task.Run(() => limiter.CheckAsync("client-a", "/orders")));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(limit, results.Count(r => r.IsAllowed));
    }
}
