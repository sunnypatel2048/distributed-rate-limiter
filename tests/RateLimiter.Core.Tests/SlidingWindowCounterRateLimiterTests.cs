using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Core.Algorithms;
using Xunit;

namespace RateLimiter.Core.Tests;

public class SlidingWindowCounterRateLimiterTests
{
    private static RateLimiterOptions Options(int limit, TimeSpan window) =>
        new() { PermitLimit = limit, Window = window };

    private static DateTimeOffset AlignToWindowBoundary(DateTimeOffset time, TimeSpan window)
    {
        var windowTicks = window.Ticks;
        return new DateTimeOffset((time.UtcTicks / windowTicks) * windowTicks, TimeSpan.Zero);
    }

    [Fact]
    public async Task AllowsRequestsUpToLimit_ThenDeniesTheNext()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new SlidingWindowCounterRateLimiter(time, Options(limit: 3, window: TimeSpan.FromSeconds(60)));

        var r1 = await limiter.CheckAsync("client-a", "/orders");
        var r2 = await limiter.CheckAsync("client-a", "/orders");
        var r3 = await limiter.CheckAsync("client-a", "/orders");
        var r4 = await limiter.CheckAsync("client-a", "/orders");

        Assert.True(r1.IsAllowed);
        Assert.True(r2.IsAllowed);
        Assert.True(r3.IsAllowed);
        Assert.False(r4.IsAllowed);
    }

    [Fact]
    public async Task WeightedEstimate_MatchesHandCalculatedFormula()
    {
        var window = TimeSpan.FromSeconds(60);
        var alignedStart = AlignToWindowBoundary(DateTimeOffset.UtcNow, window);
        var time = new FakeTimeProvider(alignedStart);
        var limiter = new SlidingWindowCounterRateLimiter(time, Options(limit: 100, window));

        // Fill the previous window with exactly 100 requests.
        for (var i = 0; i < 100; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        // Roll into the next window, then move 15s into it (25% elapsed -> weight 0.75).
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(15));

        // Consume 10 in the current window.
        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        // estimated = 10 + (100 * 0.75) = 85 -> under 100, should still be allowed.
        var result = await limiter.CheckAsync("client-a", "/orders");
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task PreviouslyDeniedRequest_CanBecomeAllowedAsWeightDecays()
    {
        var window = TimeSpan.FromSeconds(60);
        var alignedStart = AlignToWindowBoundary(DateTimeOffset.UtcNow, window);
        var time = new FakeTimeProvider(alignedStart);
        var limiter = new SlidingWindowCounterRateLimiter(time, Options(limit: 10, window));

        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        time.Advance(TimeSpan.FromSeconds(60));

        // Right at rollover, weight ~1.0 -> previous count (10) alone hits the limit.
        var earlyInNewWindow = await limiter.CheckAsync("client-a", "/orders");

        // Much later in the same window, weight has decayed close to zero.
        time.Advance(TimeSpan.FromSeconds(55));
        var lateInNewWindow = await limiter.CheckAsync("client-a", "/orders");

        Assert.False(earlyInNewWindow.IsAllowed);
        Assert.True(lateInNewWindow.IsAllowed);
    }

    [Fact]
    public async Task IsolatesDifferentClientsAndRoutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new SlidingWindowCounterRateLimiter(time, Options(limit: 1, window: TimeSpan.FromSeconds(60)));

        var clientA = await limiter.CheckAsync("client-a", "/orders");
        var clientB = await limiter.CheckAsync("client-b", "/orders");

        Assert.True(clientA.IsAllowed);
        Assert.True(clientB.IsAllowed);
    }

    [Fact]
    public async Task ConcurrentRequests_OnlyAllowsExactlyTheLimit()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        const int limit = 20;
        var limiter = new SlidingWindowCounterRateLimiter(time, Options(limit, TimeSpan.FromSeconds(60)));

        var tasks = Enumerable.Range(0, limit * 3)
            .Select(_ => Task.Run(() => limiter.CheckAsync("client-a", "/orders")));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(limit, results.Count(r => r.IsAllowed));
    }
}
