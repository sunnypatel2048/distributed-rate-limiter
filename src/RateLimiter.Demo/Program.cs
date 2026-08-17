using RateLimiter.Core;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Single shared multiplexer for the app's lifetime — StackExchange.Redis
// is thread-safe and designed to be reused, not created per-request.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("localhost:6379"));

// TimeProvider.System is the real clock in production; tests substitute
// FakeTimeProvider instead. Registering it here (rather than each algorithm
// calling DateTimeOffset.UtcNow directly) is what makes the whole chain testable.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRateLimiterFactory, RateLimiterFactory>();

var app = builder.Build();

app.MapGet("/", () => "Rate limiter demo API is running.");

// Quick sanity check for Day 1: confirms the app can actually reach Redis
// before any rate limiting logic is wired in.
app.MapGet("/redis-check", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var pong = await db.PingAsync();
    return Results.Ok(new { redis = "connected", latencyMs = pong.TotalMilliseconds });
});

// Day 2 wiring check: proves the factory + DI registration work end-to-end.
// This is NOT the real per-route caching yet — that's the middleware, next week.
app.MapGet("/rate-limit-check/{algorithm}", (RateLimiterAlgorithm algorithm, IRateLimiterFactory factory) =>
{
    var options = new RateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromSeconds(60) };
    var limiter = factory.Create(algorithm, options);
    return Results.Ok(new { algorithm = algorithm.ToString(), createdType = limiter.GetType().Name });
});

app.Run();