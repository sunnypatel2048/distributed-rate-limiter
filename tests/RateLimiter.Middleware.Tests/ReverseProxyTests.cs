using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

/// <summary>
/// Runs the real ForwardedHeadersMiddleware, configured by our extension, in front of
/// our client id resolver -- the same order as the demo app's pipeline.
/// </summary>
public class ReverseProxyTests
{
    private const string TrustedProxy = "172.28.0.10";
    private const string RealClient = "203.0.113.7";

    private static IServiceProvider Services(params string[] trustedProxies)
    {
        var settings = trustedProxies
            .Select((ip, i) => new KeyValuePair<string, string?>($"{ReverseProxyExtensions.TrustedProxiesKey}:{i}", ip));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddTrustedReverseProxies(configuration)
            .BuildServiceProvider();
    }

    private static async Task<string> ResolveClientId(IServiceProvider services, IPAddress remoteIp, string? forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp;
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        var resolver = new ApiKeyOrIpClientIdResolver(Options.Create(new RateLimitingOptions()));
        string? clientId = null;

        var forwardedHeaders = new ForwardedHeadersMiddleware(
            ctx =>
            {
                clientId = resolver.Resolve(ctx);
                return Task.CompletedTask;
            },
            NullLoggerFactory.Instance,
            services.GetRequiredService<IOptions<ForwardedHeadersOptions>>());

        await forwardedHeaders.Invoke(context);
        return clientId!;
    }

    [Fact]
    public async Task RequestFromTrustedProxy_UsesForwardedClientIp()
    {
        var clientId = await ResolveClientId(Services(TrustedProxy), IPAddress.Parse(TrustedProxy), RealClient);

        Assert.Equal($"ip:{RealClient}", clientId);
    }

    [Fact]
    public async Task TrustedProxy_SeenAsIPv4MappedIPv6_IsStillTrusted()
    {
        var mapped = IPAddress.Parse(TrustedProxy).MapToIPv6(); // ::ffff:172.28.0.10

        var clientId = await ResolveClientId(Services(TrustedProxy), mapped, RealClient);

        Assert.Equal($"ip:{RealClient}", clientId);
    }

    [Fact]
    public async Task RequestFromUntrustedSource_IgnoresForwardedHeader()
    {
        // A client talking to the app directly can't pick its own IP by sending the header.
        var attacker = IPAddress.Parse("198.51.100.9");

        var clientId = await ResolveClientId(Services(TrustedProxy), attacker, "1.2.3.4");

        Assert.Equal("ip:198.51.100.9", clientId);
    }

    [Fact]
    public async Task SpoofedEarlierHops_AreIgnored_OnlyLastHopIsUsed()
    {
        // Client sent "X-Forwarded-For: 1.2.3.4"; a proxy that appends gives "1.2.3.4, <real ip>".
        var clientId = await ResolveClientId(Services(TrustedProxy), IPAddress.Parse(TrustedProxy), $"1.2.3.4, {RealClient}");

        Assert.Equal($"ip:{RealClient}", clientId);
    }

    [Fact]
    public async Task NoTrustedProxiesConfigured_HeaderFromNonLoopbackIsIgnored()
    {
        var clientId = await ResolveClientId(Services(), IPAddress.Parse(TrustedProxy), RealClient);

        Assert.Equal($"ip:{TrustedProxy}", clientId);
    }

    [Fact]
    public void InvalidTrustedProxy_FailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Services("not-an-ip"));

        Assert.Contains("not-an-ip", ex.Message);
    }
}
