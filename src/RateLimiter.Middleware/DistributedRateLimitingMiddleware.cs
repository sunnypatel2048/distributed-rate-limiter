using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RateLimiter.Core;

namespace RateLimiter.Middleware;

public sealed class DistributedRateLimitingMiddleware(
    RequestDelegate next,
    IRateLimiterFactory factory,
    IClientIdResolver clientIdResolver,
    IOptions<RateLimitingOptions> options,
    ILogger<DistributedRateLimitingMiddleware> logger)
{
    // One limiter per policy for the app's lifetime. Required for the in-memory
    // limiters (their state lives in the object); harmless for the Redis ones.
    private readonly ConcurrentDictionary<string, IRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var policy = RateLimitPolicyMatcher.Match(options.Value.Policies, context.Request.Path);
        if (policy is null)
        {
            await next(context);
            return;
        }

        var limiter = _limiters.GetOrAdd(policy.Name, _ => factory.Create(
            policy.Algorithm,
            new RateLimiterOptions { PermitLimit = policy.PermitLimit, Window = policy.Window }));

        var clientId = clientIdResolver.Resolve(context);

        RateLimitResult result;
        try
        {
            result = await limiter.CheckAsync(clientId, policy.Name, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any limiter failure (Redis down, timeout, ...) lands here. Catching broadly
            // is deliberate: the rate limiter must never be the reason the API errors out
            // unless the policy explicitly asks for that.
            if (policy.FailureMode == FailureMode.FailOpen)
            {
                logger.LogWarning(ex, "Rate limiter unavailable for policy {Policy}; failing open.", policy.Name);
                await next(context);
                return;
            }

            logger.LogError(ex, "Rate limiter unavailable for policy {Policy}; failing closed.", policy.Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "rate_limiter_unavailable" });
            return;
        }

        context.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString(CultureInfo.InvariantCulture);

        if (result.IsAllowed)
        {
            await next(context);
            return;
        }

        // Retry-After is whole seconds; round up and never send 0 (which invites an instant retry).
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new { error = "rate_limited", policy = policy.Name, retryAfterSeconds });
    }
}
