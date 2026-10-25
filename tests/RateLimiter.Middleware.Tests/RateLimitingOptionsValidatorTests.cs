using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

public class RateLimitingOptionsValidatorTests
{
    private static RateLimitPolicy ValidPolicy(string name = "orders") =>
        new() { Name = name, PathPrefix = "/api/orders", PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    private static RateLimitingOptions With(params RateLimitPolicy[] policies) => new() { Policies = [.. policies] };

    [Fact]
    public void ValidOptions_Succeed()
    {
        var result = new RateLimitingOptionsValidator().Validate(null, With(ValidPolicy("a"), ValidPolicy("b")));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void DuplicatePolicyNames_Fail_CaseInsensitively()
    {
        var result = new RateLimitingOptionsValidator().Validate(null, With(ValidPolicy("orders"), ValidPolicy("ORDERS")));

        Assert.True(result.Failed);
        Assert.Contains("more than once", result.FailureMessage);
    }

    public static TheoryData<Action<RateLimitPolicy>, string> InvalidPolicies => new()
    {
        { p => p.Name = "", "needs a Name" },
        { p => p.PathPrefix = "api/orders", "must start with '/'" },
        { p => p.PermitLimit = 0, "PermitLimit" },
        { p => p.Window = TimeSpan.Zero, "Window" },
        { p => p.Algorithm = (RateLimiter.Core.RateLimiterAlgorithm)999, "unknown Algorithm" }
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void InvalidPolicy_Fails(Action<RateLimitPolicy> breakIt, string expectedMessagePart)
    {
        var policy = ValidPolicy();
        breakIt(policy);

        var result = new RateLimitingOptionsValidator().Validate(null, With(policy));

        Assert.True(result.Failed);
        Assert.Contains(expectedMessagePart, result.FailureMessage);
    }
}
