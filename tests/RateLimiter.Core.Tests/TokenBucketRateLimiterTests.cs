using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Core.Algorithms;
using Xunit;

namespace RateLimiter.Core.Tests;

public class TokenBucketRateLimiterTests
{
    private static RateLimiterOptions Options(int capacity, TimeSpan window) =>
        new() { PermitLimit = capacity, Window = window };

    [Fact]
    public async Task AllowsFullBurstImmediately_ThenDeniesTheNext()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TokenBucketRateLimiter(time, Options(capacity: 10, window: TimeSpan.FromSeconds(60)));

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 11; i++)
        {
            results.Add(await limiter.CheckAsync("client-a", "/orders"));
        }

        Assert.True(results.Take(10).All(r => r.IsAllowed));
        Assert.False(results[10].IsAllowed);
    }

    [Fact]
    public async Task RefillsAtExpectedRate_MatchesHandCalculatedTokens()
    {
        // capacity 10 over 60s -> refill rate = 1/6 token/sec
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TokenBucketRateLimiter(time, Options(capacity: 10, window: TimeSpan.FromSeconds(60)));

        for (var i = 0; i < 10; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        var deniedWhileEmpty = await limiter.CheckAsync("client-a", "/orders");
        Assert.False(deniedWhileEmpty.IsAllowed);

        // 30s at 1/6 token/sec = exactly 5 tokens earned.
        time.Advance(TimeSpan.FromSeconds(30));

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 6; i++)
        {
            results.Add(await limiter.CheckAsync("client-a", "/orders"));
        }

        Assert.True(results.Take(5).All(r => r.IsAllowed));
        Assert.False(results[5].IsAllowed);
    }

    [Fact]
    public async Task TokensDoNotAccumulateBeyondCapacity()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TokenBucketRateLimiter(time, Options(capacity: 5, window: TimeSpan.FromSeconds(60)));

        // Drain fully first so there's actually room for refill to occur.
        for (var i = 0; i < 5; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        // Idle for an hour -- far longer than the 60s needed to fully refill from empty.
        time.Advance(TimeSpan.FromHours(1));

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 6; i++)
        {
            results.Add(await limiter.CheckAsync("client-a", "/orders"));
        }

        // If tokens accumulated unbounded, this would allow far more than 5.
        Assert.True(results.Take(5).All(r => r.IsAllowed));
        Assert.False(results[5].IsAllowed);
    }

    [Fact]
    public async Task SteadyStateAtRefillRate_NeverGetsFullyDenied()
    {
        // Consuming exactly one token every "one refill interval" should sustain
        // indefinitely -- a meaningfully different behavior than either window algorithm.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var window = TimeSpan.FromSeconds(60);
        const int capacity = 10;
        var limiter = new TokenBucketRateLimiter(time, Options(capacity, window));
        var intervalPerToken = TimeSpan.FromSeconds(window.TotalSeconds / capacity);

        // Drain the initial burst capacity first so we're testing steady state, not the burst.
        for (var i = 0; i < capacity; i++)
        {
            await limiter.CheckAsync("client-a", "/orders");
        }

        for (var i = 0; i < 20; i++)
        {
            time.Advance(intervalPerToken);
            var result = await limiter.CheckAsync("client-a", "/orders");
            Assert.True(result.IsAllowed);
        }
    }

    [Fact]
    public async Task IsolatesDifferentClientsAndRoutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new TokenBucketRateLimiter(time, Options(capacity: 1, window: TimeSpan.FromSeconds(60)));

        var clientA = await limiter.CheckAsync("client-a", "/orders");
        var clientB = await limiter.CheckAsync("client-b", "/orders");

        Assert.True(clientA.IsAllowed);
        Assert.True(clientB.IsAllowed);
    }

    [Fact]
    public async Task ConcurrentRequests_OnlyAllowsExactlyCapacity()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        const int capacity = 20;
        var limiter = new TokenBucketRateLimiter(time, Options(capacity, TimeSpan.FromSeconds(60)));

        var tasks = Enumerable.Range(0, capacity * 3)
            .Select(_ => Task.Run(() => limiter.CheckAsync("client-a", "/orders")));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(capacity, results.Count(r => r.IsAllowed));
    }
}
