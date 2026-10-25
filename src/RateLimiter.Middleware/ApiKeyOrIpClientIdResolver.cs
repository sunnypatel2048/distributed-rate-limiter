using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace RateLimiter.Middleware;

/// <summary>
/// Uses the API key header if present, otherwise the client IP.
///
/// Two deliberate details:
///  - The API key is HASHED before use. The client id ends up in Redis key names,
///    and raw secrets shouldn't sit in plain text in another datastore.
///  - The key is NOT validated here. An unauthenticated caller could rotate made-up
///    keys to get fresh quota. In a real system, put authentication in front of this
///    middleware and resolve the client from the authenticated identity instead
///    (swap in a different IClientIdResolver).
///
/// Behind a load balancer, RemoteIpAddress is the balancer's address, not the
/// client's -- configure ForwardedHeadersMiddleware before this middleware.
/// </summary>
public sealed class ApiKeyOrIpClientIdResolver(IOptions<RateLimitingOptions> options) : IClientIdResolver
{
    public string Resolve(HttpContext context)
    {
        var apiKey = context.Request.Headers[options.Value.ApiKeyHeader].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
            return $"key:{Convert.ToHexString(hash, 0, 16)}";
        }

        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "ip:unknown" : $"ip:{ip}";
    }
}
