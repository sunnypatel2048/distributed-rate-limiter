using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using RateLimiter.Core;
using RateLimiter.Middleware;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var instanceName = builder.Configuration["InstanceName"] ?? Environment.MachineName;

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

// Metrics: the rate limiter's own meter plus ASP.NET Core's built-in HTTP metrics,
// exposed in Prometheus format at /metrics. Each replica reports only its own
// numbers; Prometheus scrapes all of them and sums for cluster-wide totals.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("ratelimiter-demo", serviceInstanceId: instanceName))
    .WithMetrics(metrics => metrics
        .AddMeter(RateLimiterMetrics.MeterName)
        .AddMeter("Microsoft.AspNetCore.Hosting")
        // The default histogram buckets go 0, 5, 10, 25 ... -- sized for milliseconds.
        // A limiter check takes ~0.1 ms (0.0001 s), so every sample would land in the
        // first bucket and percentiles would be meaningless. Use sub-millisecond buckets.
        .AddView("ratelimiter.check.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0.00005, 0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25]
        })
        .AddPrometheusExporter());

var app = builder.Build();

// Tag every response -- including 429s -- with the instance that handled it, so
// you can watch the load balancer spread one client's requests across replicas
// while the shared Redis enforces a single combined limit.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Served-By"] = instanceName;
    await next(context);
});

// Order matters: resolve the real client IP first, THEN rate limit on it.
app.UseTrustedReverseProxies();
app.UseDistributedRateLimiting();

// Prometheus scrape endpoint. Not under /api, so it's never rate limited.
// nginx refuses /metrics from outside; Prometheus scrapes each replica directly.
app.MapPrometheusScrapingEndpoint();

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
