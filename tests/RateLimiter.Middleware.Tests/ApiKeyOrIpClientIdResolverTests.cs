using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

public class ApiKeyOrIpClientIdResolverTests
{
    private static readonly ApiKeyOrIpClientIdResolver Resolver = new(Options.Create(new RateLimitingOptions()));

    private static DefaultHttpContext Context(string? apiKey = null, string? ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        if (apiKey is not null) context.Request.Headers["X-Api-Key"] = apiKey;
        if (ip is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public void ApiKey_IsPreferredOverIp_AndHashed()
    {
        var clientId = Resolver.Resolve(Context(apiKey: "super-secret-key"));

        Assert.StartsWith("key:", clientId);
        Assert.DoesNotContain("super-secret-key", clientId);
    }

    [Fact]
    public void SameApiKey_FromDifferentIps_IsTheSameClient()
    {
        Assert.Equal(
            Resolver.Resolve(Context(apiKey: "abc", ip: "203.0.113.7")),
            Resolver.Resolve(Context(apiKey: "abc", ip: "198.51.100.9")));
    }

    [Fact]
    public void DifferentApiKeys_AreDifferentClients()
    {
        Assert.NotEqual(Resolver.Resolve(Context(apiKey: "abc")), Resolver.Resolve(Context(apiKey: "xyz")));
    }

    [Fact]
    public void NoApiKey_FallsBackToIp()
    {
        Assert.Equal("ip:203.0.113.7", Resolver.Resolve(Context()));
    }

    [Fact]
    public void NoApiKeyAndNoIp_IsUnknown()
    {
        Assert.Equal("ip:unknown", Resolver.Resolve(Context(ip: null)));
    }

    [Fact]
    public void HeaderName_IsConfigurable()
    {
        var resolver = new ApiKeyOrIpClientIdResolver(Options.Create(new RateLimitingOptions { ApiKeyHeader = "X-Client-Token" }));
        var context = Context();
        context.Request.Headers["X-Client-Token"] = "abc";

        Assert.StartsWith("key:", resolver.Resolve(context));
    }
}
