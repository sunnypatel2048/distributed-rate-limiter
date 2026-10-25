using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace RateLimiter.Middleware.Tests.EndToEnd;

/// <summary>
/// Boots the real demo app in memory (real appsettings.json policies, real middleware,
/// real Redis-backed limiters). The only change: Redis points at the test container.
/// Each instance of this factory is an independent "app instance" with its own
/// middleware and its own Redis connection.
/// </summary>
public sealed class DemoAppFactory(string redisConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
        });
    }
}
