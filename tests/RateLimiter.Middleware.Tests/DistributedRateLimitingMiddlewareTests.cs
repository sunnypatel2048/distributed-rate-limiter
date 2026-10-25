using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

/// <summary>
/// Pure unit tests: the in-memory RateLimiterFactory (or a stub) stands in for Redis,
/// and DefaultHttpContext stands in for a real request. No Docker, runs in milliseconds.
/// </summary>
public class DistributedRateLimitingMiddlewareTests
{
    private sealed class NextProbe
    {
        public int Calls { get; private set; }

        public Task Invoke(HttpContext context)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFactory(IRateLimiter limiter) : IRateLimiterFactory
    {
        public IRateLimiter Create(RateLimiterAlgorithm algorithm, RateLimiterOptions options) => limiter;
    }

    private sealed class StubLimiter(Func<Task<RateLimitResult>> check) : IRateLimiter
    {
        public Task<RateLimitResult> CheckAsync(string clientId, string route, CancellationToken cancellationToken = default) => check();
    }

    private static RateLimitPolicy Policy(string name, string prefix, int limit = 2, FailureMode failureMode = FailureMode.FailOpen) =>
        new()
        {
            Name = name,
            PathPrefix = prefix,
            Algorithm = RateLimiterAlgorithm.FixedWindow,
            PermitLimit = limit,
            Window = TimeSpan.FromSeconds(60),
            FailureMode = failureMode
        };

    private static (DistributedRateLimitingMiddleware Middleware, NextProbe Next) Create(
        IRateLimiterFactory? factory = null,
        params RateLimitPolicy[] policies)
    {
        var options = Options.Create(new RateLimitingOptions { Policies = [.. policies] });
        var next = new NextProbe();
        var middleware = new DistributedRateLimitingMiddleware(
            next.Invoke,
            factory ?? new RateLimiterFactory(new FakeTimeProvider(DateTimeOffset.UtcNow)),
            new ApiKeyOrIpClientIdResolver(options),
            options,
            NullLogger<DistributedRateLimitingMiddleware>.Instance);
        return (middleware, next);
    }

    private static DefaultHttpContext Request(string path, string ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task AllowedRequest_CallsNext_AndSetsRateLimitHeaders()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 2)]);
        var context = Request("/api/orders");

        await middleware.InvokeAsync(context);

        Assert.Equal(1, next.Calls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("2", context.Response.Headers["X-RateLimit-Limit"].ToString());
        Assert.Equal("1", context.Response.Headers["X-RateLimit-Remaining"].ToString());
    }

    [Fact]
    public async Task OverLimit_Returns429_WithRetryAfter_AndDoesNotCallNext()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 1)]);
        await middleware.InvokeAsync(Request("/api/orders"));

        var denied = Request("/api/orders");
        await middleware.InvokeAsync(denied);

        Assert.Equal(1, next.Calls);
        Assert.Equal(StatusCodes.Status429TooManyRequests, denied.Response.StatusCode);
        Assert.Equal("0", denied.Response.Headers["X-RateLimit-Remaining"].ToString());
        Assert.True(int.Parse(denied.Response.Headers.RetryAfter.ToString()) > 0);
        Assert.Contains("rate_limited", Body(denied));
    }

    [Fact]
    public async Task UnmatchedRoute_IsNotRateLimited()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 1)]);

        for (var i = 0; i < 5; i++)
        {
            var context = Request("/health");
            await middleware.InvokeAsync(context);
            Assert.False(context.Response.Headers.ContainsKey("X-RateLimit-Limit"));
        }

        Assert.Equal(5, next.Calls);
    }

    [Fact]
    public async Task PathsUnderOnePolicy_ShareOneQuota()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 1)]);
        await middleware.InvokeAsync(Request("/api/orders"));

        var otherPathSamePolicy = Request("/api/orders/42");
        await middleware.InvokeAsync(otherPathSamePolicy);

        Assert.Equal(StatusCodes.Status429TooManyRequests, otherPathSamePolicy.Response.StatusCode);
    }

    [Fact]
    public async Task DifferentPolicies_HaveIndependentQuotas()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 1), Policy("search", "/api/search", limit: 1)]);
        await middleware.InvokeAsync(Request("/api/orders"));

        var search = Request("/api/search");
        await middleware.InvokeAsync(search);

        Assert.Equal(StatusCodes.Status200OK, search.Response.StatusCode);
        Assert.Equal(2, next.Calls);
    }

    [Fact]
    public async Task DifferentClients_HaveIndependentQuotas()
    {
        var (middleware, next) = Create(policies: [Policy("orders", "/api/orders", limit: 1)]);
        await middleware.InvokeAsync(Request("/api/orders", ip: "203.0.113.7"));

        var otherClient = Request("/api/orders", ip: "198.51.100.9");
        await middleware.InvokeAsync(otherClient);

        Assert.Equal(StatusCodes.Status200OK, otherClient.Response.StatusCode);
    }

    [Fact]
    public async Task LimiterFailure_FailOpen_LetsRequestThrough()
    {
        var failing = new StubFactory(new StubLimiter(() => throw new InvalidOperationException("Redis is down")));
        var (middleware, next) = Create(failing, Policy("orders", "/api/orders", failureMode: FailureMode.FailOpen));
        var context = Request("/api/orders");

        await middleware.InvokeAsync(context);

        Assert.Equal(1, next.Calls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task LimiterFailure_FailClosed_Returns503()
    {
        var failing = new StubFactory(new StubLimiter(() => throw new InvalidOperationException("Redis is down")));
        var (middleware, next) = Create(failing, Policy("orders", "/api/orders", failureMode: FailureMode.FailClosed));
        var context = Request("/api/orders");

        await middleware.InvokeAsync(context);

        Assert.Equal(0, next.Calls);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("rate_limiter_unavailable", Body(context));
    }

    [Fact]
    public async Task ClientDisconnect_IsNotSwallowedAsALimiterFailure()
    {
        // OperationCanceledException means the caller went away, not that Redis is down;
        // it must propagate instead of triggering fail-open/fail-closed handling.
        var cancelled = new StubFactory(new StubLimiter(() => throw new OperationCanceledException()));
        var (middleware, next) = Create(cancelled, Policy("orders", "/api/orders"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(Request("/api/orders")));
        Assert.Equal(0, next.Calls);
    }

    [Fact]
    public async Task SubSecondRetryAfter_IsRoundedUpToOneSecond()
    {
        var denied = new StubFactory(new StubLimiter(() =>
            Task.FromResult(RateLimitResult.Denied(10, TimeSpan.FromMilliseconds(200)))));
        var (middleware, _) = Create(denied, Policy("orders", "/api/orders"));
        var context = Request("/api/orders");

        await middleware.InvokeAsync(context);

        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
    }
}
