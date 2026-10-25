using Microsoft.AspNetCore.Http;

namespace RateLimiter.Middleware;

public static class RateLimitPolicyMatcher
{
    /// <summary>
    /// Returns the policy with the longest PathPrefix that matches the request path,
    /// or null if none match. Longest-prefix wins so specific routes can override a
    /// "/" catch-all.
    /// </summary>
    public static RateLimitPolicy? Match(IEnumerable<RateLimitPolicy> policies, PathString path)
    {
        RateLimitPolicy? best = null;

        foreach (var policy in policies)
        {
            var matches = policy.PathPrefix == "/"
                || path.StartsWithSegments(policy.PathPrefix, StringComparison.OrdinalIgnoreCase);

            if (matches && (best is null || policy.PathPrefix.Length > best.PathPrefix.Length))
            {
                best = policy;
            }
        }

        return best;
    }
}
