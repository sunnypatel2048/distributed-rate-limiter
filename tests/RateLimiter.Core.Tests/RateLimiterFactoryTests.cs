using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Core.Algorithms;
using Xunit;

namespace RateLimiter.Core.Tests;

public class RateLimiterFactoryTests
{
    private static RateLimiterOptions Options() =>
        new() { PermitLimit = 10, Window = TimeSpan.FromSeconds(60) };

    [Theory]
    [InlineData(RateLimiterAlgorithm.FixedWindow, typeof(FixedWindowRateLimiter))]
    [InlineData(RateLimiterAlgorithm.SlidingWindowCounter, typeof(SlidingWindowCounterRateLimiter))]
    [InlineData(RateLimiterAlgorithm.TokenBucket, typeof(TokenBucketRateLimiter))]
    public void Create_ReturnsCorrectConcreteType(RateLimiterAlgorithm algorithm, Type expectedType)
    {
        var factory = new RateLimiterFactory(new FakeTimeProvider(DateTimeOffset.UtcNow));

        var limiter = factory.Create(algorithm, Options());

        Assert.IsType(expectedType, limiter);
    }

    [Fact]
    public void Create_UnrecognizedAlgorithm_Throws()
    {
        var factory = new RateLimiterFactory(new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Create((RateLimiterAlgorithm)999, Options()));
    }
}