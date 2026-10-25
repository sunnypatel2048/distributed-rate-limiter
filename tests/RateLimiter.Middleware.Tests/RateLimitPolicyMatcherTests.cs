using Microsoft.AspNetCore.Http;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

public class RateLimitPolicyMatcherTests
{
    private static RateLimitPolicy Policy(string name, string prefix) =>
        new() { Name = name, PathPrefix = prefix, PermitLimit = 1, Window = TimeSpan.FromSeconds(1) };

    private static readonly RateLimitPolicy[] Policies =
    [
        Policy("default", "/"),
        Policy("api", "/api"),
        Policy("orders", "/api/orders")
    ];

    [Theory]
    [InlineData("/api/orders", "orders")]
    [InlineData("/api/orders/42", "orders")]
    [InlineData("/API/Orders", "orders")]
    [InlineData("/api/products", "api")]
    [InlineData("/api/ordersX", "api")]
    [InlineData("/health", "default")]
    public void LongestMatchingPrefixWins(string path, string expectedPolicy)
    {
        var match = RateLimitPolicyMatcher.Match(Policies, new PathString(path));

        Assert.Equal(expectedPolicy, match?.Name);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var match = RateLimitPolicyMatcher.Match([Policy("orders", "/api/orders")], new PathString("/health"));

        Assert.Null(match);
    }
}
