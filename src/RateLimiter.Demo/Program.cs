using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Single shared multiplexer for the app's lifetime — StackExchange.Redis
// is thread-safe and designed to be reused, not created per-request.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("localhost:6379"));

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

app.Run();
