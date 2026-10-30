using RateLimiter.Core;
using RateLimiter.Middleware;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Single shared multiplexer for the app's lifetime — StackExchange.Redis
// is thread-safe and designed to be reused, not created per-request.
// AbortOnConnectFail=false lets the app start even if Redis isn't up yet;
// the multiplexer keeps retrying in the background (and the middleware
// applies each policy's FailureMode in the meantime).
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisOptions = ConfigurationOptions.Parse(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
    redisOptions.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(redisOptions);
});

builder.Services.AddSingleton(TimeProvider.System);

// Storage backend is chosen here, in one line. Swap to RateLimiterFactory for
// single-instance, in-memory limiting (e.g. local dev without Docker).
builder.Services.AddSingleton<IRateLimiterFactory, RedisRateLimiterFactory>();

// Policies come from the "RateLimiting" section of appsettings.json and are
// validated at startup.
builder.Services.AddDistributedRateLimiting(builder.Configuration);

// Behind nginx, trust X-Forwarded-For only from the proxies listed in
// ReverseProxy:TrustedProxies (set by docker-compose; empty for local dev).
builder.Services.AddTrustedReverseProxies(builder.Configuration);

var app = builder.Build();

// Tag every response -- including 429s -- with the instance that handled it, so
// you can watch the load balancer spread one client's requests across replicas
// while the shared Redis enforces a single combined limit.
var instanceName = builder.Configuration["InstanceName"] ?? Environment.MachineName;
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Served-By"] = instanceName;
    await next(context);
});

// Order matters: resolve the real client IP first, THEN rate limit on it.
app.UseTrustedReverseProxies();
app.UseDistributedRateLimiting();

// Not under /api, so no policy matches: never rate limited.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/redis-check", async (IConnectionMultiplexer redis) =>
{
    var pong = await redis.GetDatabase().PingAsync();
    return Results.Ok(new { redis = "connected", latencyMs = pong.TotalMilliseconds });
});

var api = app.MapGroup("/api");

// "orders" policy: Token Bucket, tolerates short bursts.
api.MapGet("/orders", () => Results.Ok(new[]
{
    new { id = 1, item = "Mechanical keyboard", quantity = 1 },
    new { id = 2, item = "USB-C hub", quantity = 2 }
}));
api.MapGet("/orders/{id:int}", (int id) => Results.Ok(new { id, item = "Mechanical keyboard", quantity = 1 }));

// "search" policy: Sliding Window, and FailClosed -- an expensive endpoint we'd
// rather turn off than leave unprotected while Redis is down.
api.MapGet("/search", (string? q) => Results.Ok(new { query = q ?? string.Empty, results = Array.Empty<string>() }));

// Everything else under /api falls through to the "api-default" policy.
api.MapGet("/products", () => Results.Ok(new[] { new { id = 1, name = "Mechanical keyboard" } }));

app.Run();

// Exposes the implicit Program class to WebApplicationFactory<Program> in the end-to-end tests.
public partial class Program;
