using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RateLimiter.Middleware;

/// <summary>
/// Behind a load balancer, every request's RemoteIpAddress is the load balancer's
/// address, so IP-based rate limiting would put ALL clients in one shared bucket.
/// ForwardedHeadersMiddleware fixes that by reading X-Forwarded-For -- but that header
/// is just text any client can send, so it must only be honoured when the request
/// actually came from a proxy we trust. Anyone else sending it is ignored.
///
/// Config: "ReverseProxy:TrustedProxies": [ "172.28.0.10" ]
/// With no trusted proxies configured, only the framework defaults (loopback) apply,
/// which is the right behaviour for local development.
/// </summary>
public static class ReverseProxyExtensions
{
    public const string TrustedProxiesKey = "ReverseProxy:TrustedProxies";

    public static IServiceCollection AddTrustedReverseProxies(this IServiceCollection services, IConfiguration configuration)
    {
        // Parsed eagerly so a typo in config fails at startup, not on the first request.
        var trustedProxies = new List<IPAddress>();
        foreach (var value in configuration.GetSection(TrustedProxiesKey).Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                throw new InvalidOperationException($"{TrustedProxiesKey} contains an invalid IP address: '{value}'.");
            }

            trustedProxies.Add(address);

            // Kestrel listens on IPv6 ([::]) inside containers, so an IPv4 peer can show up
            // as ::ffff:172.28.0.10. Trust both spellings of the same address.
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                trustedProxies.Add(address.MapToIPv6());
            }
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Only the LAST hop is used: the one our proxy appended. Earlier entries in
            // X-Forwarded-For were written by the client and can't be trusted.
            options.ForwardLimit = 1;

            foreach (var proxy in trustedProxies)
            {
                options.KnownProxies.Add(proxy);
            }
        });

        return services;
    }

    /// <summary>Must run BEFORE UseDistributedRateLimiting, or the limiter sees the proxy's IP.</summary>
    public static IApplicationBuilder UseTrustedReverseProxies(this IApplicationBuilder app) =>
        app.UseForwardedHeaders();
}
