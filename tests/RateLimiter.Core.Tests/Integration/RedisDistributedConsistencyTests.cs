using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core.Redis;
using StackExchange.Redis;

namespace RateLimiter.Core.Tests.Integration;

/// <summary>
/// The headline test for the whole project: several "app instances" -- each with its
/// own Redis connection and its own limiter object, exactly like separate processes
/// behind a load balancer -- hammer the same client concurrently. The combined
/// number of admitted requests must equal the limit exactly. A naive
/// GET-then-INCR implementation over-admits here.
/// </summary>
[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class RedisDistributedConsistencyTests(RedisFixture redis)
{
    [Theory]
    [InlineData(RateLimiterAlgorithm.FixedWindow)]
    [InlineData(RateLimiterAlgorithm.SlidingWindowCounter)]
    [InlineData(RateLimiterAlgorithm.TokenBucket)]
    public async Task ConcurrentRequestsAcrossInstances_AdmitExactlyTheLimit(RateLimiterAlgorithm algorithm)
    {
        const int limit = 20;
        const int instanceCount = 3;
        const int requestsPerInstance = limit * 2;
        var options = new RateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromSeconds(60) };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = RedisFixture.UniqueClientId();

        var connections = new List<ConnectionMultiplexer>();
        try
        {
            var instances = new List<IRateLimiter>();
            for (var i = 0; i < instanceCount; i++)
            {
                var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
                connections.Add(connection);
                instances.Add(new RedisRateLimiterFactory(connection, time).Create(algorithm, options));
            }

            var tasks = instances.SelectMany(instance => Enumerable.Range(0, requestsPerInstance)
                .Select(_ => Task.Run(() => instance.CheckAsync(client, "/orders"))));
            var results = await Task.WhenAll(tasks);

            Assert.Equal(limit, results.Count(r => r.IsAllowed));
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(RateLimiterAlgorithm.FixedWindow, typeof(RedisFixedWindowRateLimiter))]
    [InlineData(RateLimiterAlgorithm.SlidingWindowCounter, typeof(RedisSlidingWindowCounterRateLimiter))]
    [InlineData(RateLimiterAlgorithm.TokenBucket, typeof(RedisTokenBucketRateLimiter))]
    public void Factory_ReturnsCorrectRedisType(RateLimiterAlgorithm algorithm, Type expectedType)
    {
        var factory = new RedisRateLimiterFactory(redis.Connection, TimeProvider.System);
        var options = new RateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromSeconds(60) };

        Assert.IsType(expectedType, factory.Create(algorithm, options));
    }
}
