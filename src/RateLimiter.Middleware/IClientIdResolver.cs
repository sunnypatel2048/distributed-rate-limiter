using Microsoft.AspNetCore.Http;

namespace RateLimiter.Middleware;

/// <summary>
/// Decides "who is this request from" for rate limiting purposes. Swappable via DI,
/// e.g. to key on an authenticated user id instead.
/// </summary>
public interface IClientIdResolver
{
    string Resolve(HttpContext context);
}
