using RateLimiter.Core;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Single shared multiplexer for the app's lifetime — StackExchange.Redis
// is thread-safe and designed to be reused, not created per-request.
// AbortOnConnectFail=false lets the app start even if Redis isn't up yet;
// the multiplexer keeps retrying in the background.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisOptions = ConfigurationOptions.Parse(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
    redisOptions.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(redisOptions);
});

// TimeProvider.System is the real clock in production; tests substitute
// FakeTimeProvider instead. Registering it here (rather than each algorithm
// calling DateTimeOffset.UtcNow directly) is what makes the whole chain testable.
builder.Services.AddSingleton(TimeProvider.System);

// Storage backend is chosen here, in one line. Swap to RateLimiterFactory for
// single-instance, in-memory limiting (e.g. local dev without Docker).
builder.Services.AddSingleton<IRateLimiterFactory, RedisRateLimiterFactory>();

var app = builder.Build();

app.MapGet("/", () => "Rate limiter demo API is running.");

// Quick sanity check: confirms the app can actually reach Redis.
app.MapGet("/redis-check", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var pong = await db.PingAsync();
    return Results.Ok(new { redis = "connected", latencyMs = pong.TotalMilliseconds });
});

// Manual end-to-end check of the Redis-backed limiters: 5 requests per 60s.
// Try: for i in {1..6}; do curl -i "localhost:<port>/rate-limit-check/TokenBucket?client=me"; done
// The 6th call returns 429 with a Retry-After header. This is still NOT the real
// middleware — that's next week — it just exercises the limiter directly.
app.MapGet("/rate-limit-check/{algorithm}", async (
    RateLimiterAlgorithm algorithm,
    string? client,
    IRateLimiterFactory factory,
    HttpContext http) =>
{
    var options = new RateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromSeconds(60) };
    var limiter = factory.Create(algorithm, options);
    var result = await limiter.CheckAsync(client ?? "anonymous", $"/rate-limit-check/{algorithm}");

    http.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString();
    http.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString();

    if (!result.IsAllowed)
    {
        http.Response.Headers.RetryAfter = Math.Ceiling(result.RetryAfter.TotalSeconds).ToString();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    return Results.Ok(new { algorithm = algorithm.ToString(), allowed = true, remaining = result.Remaining });
});

app.Run();
