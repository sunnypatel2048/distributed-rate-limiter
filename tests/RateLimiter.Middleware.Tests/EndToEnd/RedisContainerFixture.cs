using Testcontainers.Redis;

namespace RateLimiter.Middleware.Tests.EndToEnd;

/// <summary>One throwaway Redis container per test class. Requires Docker.</summary>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
