using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace RateLimiter.Middleware;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers options (validated at startup) and the default client id resolver.
    /// The caller still chooses the storage backend by registering an
    /// IRateLimiterFactory (RedisRateLimiterFactory or the in-memory RateLimiterFactory).
    /// </summary>
    public static IServiceCollection AddDistributedRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>());
        services.TryAddSingleton<IClientIdResolver, ApiKeyOrIpClientIdResolver>();

        return services;
    }

    public static IApplicationBuilder UseDistributedRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<DistributedRateLimitingMiddleware>();
}
