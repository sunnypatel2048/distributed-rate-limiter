using StackExchange.Redis;
using Testcontainers.Redis;

namespace RateLimiter.Core.Tests.Integration;

/// <summary>
/// Starts ONE throwaway Redis container for the whole integration test run (shared
/// via an xUnit collection fixture) and tears it down afterwards. Requires Docker
/// to be running. Each test uses a unique client id, so tests never see each
/// other's keys even though they share the container.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public ConnectionMultiplexer Connection { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        Connection.Dispose();
        await _container.DisposeAsync();
    }

    public static string UniqueClientId() => $"client-{Guid.NewGuid():N}";
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
