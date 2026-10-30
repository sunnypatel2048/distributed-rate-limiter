using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RateLimiter.Core;
using RateLimiter.Middleware;

namespace RateLimiter.Middleware.Tests;

public class RateLimiterObservabilityTests
{
    private sealed class FailingFactory : IRateLimiterFactory
    {
        public IRateLimiter Create(RateLimiterAlgorithm algorithm, RateLimiterOptions options) => new FailingLimiter();
    }

    private sealed class FailingLimiter : IRateLimiter
    {
        public Task<RateLimitResult> CheckAsync(string clientId, string route, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Redis is down");
    }

    /// <summary>Keeps every log entry, including its structured fields.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> Fields, Exception? Exception);

        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new Entry(logLevel, eventId, fields, exception));
        }
    }

    private static RateLimitPolicy Policy(FailureMode failureMode = FailureMode.FailOpen) => new()
    {
        Name = "orders",
        PathPrefix = "/api/orders",
        Algorithm = RateLimiterAlgorithm.TokenBucket,
        PermitLimit = 1,
        Window = TimeSpan.FromSeconds(60),
        FailureMode = failureMode
    };

    private static DistributedRateLimitingMiddleware Create(
        RateLimitPolicy policy,
        RateLimiterMetrics metrics,
        ILogger<DistributedRateLimitingMiddleware> logger,
        IRateLimiterFactory? factory = null)
    {
        var options = Options.Create(new RateLimitingOptions { Policies = [policy] });
        return new DistributedRateLimitingMiddleware(
            _ => Task.CompletedTask,
            factory ?? new RateLimiterFactory(new FakeTimeProvider(DateTimeOffset.UtcNow)),
            new ApiKeyOrIpClientIdResolver(options),
            options,
            metrics,
            logger);
    }

    private static DefaultHttpContext Request(string path = "/api/orders")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task AllowedAndRejected_AreCountedWithPolicyAlgorithmAndOutcomeTags()
    {
        var meterFactory = TestMetrics.NewMeterFactory();
        using var recorder = new MetricsRecorder(meterFactory);
        var middleware = Create(Policy(), new RateLimiterMetrics(meterFactory), new CapturingLogger<DistributedRateLimitingMiddleware>());

        await middleware.InvokeAsync(Request()); // allowed (limit 1)
        await middleware.InvokeAsync(Request()); // rejected

        var decisions = recorder.For("ratelimiter.decisions");
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, d =>
        {
            Assert.Equal(1, d.Value);
            Assert.Equal("orders", d.Tags["policy"]);
            Assert.Equal("TokenBucket", d.Tags["algorithm"]);
        });
        Assert.Equal(new[] { "allowed", "rejected" }, decisions.Select(d => d.Tags["outcome"]!.ToString()));
    }

    [Fact]
    public async Task ClientId_IsNeverAMetricTag()
    {
        // Unbounded tag values (one per client) would explode the number of time series.
        var meterFactory = TestMetrics.NewMeterFactory();
        using var recorder = new MetricsRecorder(meterFactory);
        var middleware = Create(Policy(), new RateLimiterMetrics(meterFactory), new CapturingLogger<DistributedRateLimitingMiddleware>());

        await middleware.InvokeAsync(Request());

        var tagKeys = recorder.For("ratelimiter.decisions").Single().Tags.Keys;
        Assert.Equal(new[] { "algorithm", "outcome", "policy" }, tagKeys.Order());
    }

    [Fact]
    public async Task CheckDuration_IsRecordedForEveryDecision()
    {
        var meterFactory = TestMetrics.NewMeterFactory();
        using var recorder = new MetricsRecorder(meterFactory);
        var middleware = Create(Policy(), new RateLimiterMetrics(meterFactory), new CapturingLogger<DistributedRateLimitingMiddleware>());

        await middleware.InvokeAsync(Request());
        await middleware.InvokeAsync(Request());

        var durations = recorder.For("ratelimiter.check.duration");
        Assert.Equal(2, durations.Count);
        Assert.All(durations, d => Assert.InRange(d.Value, 0.0, 5.0));
    }

    [Fact]
    public async Task UnmatchedRoute_RecordsNothing()
    {
        var meterFactory = TestMetrics.NewMeterFactory();
        using var recorder = new MetricsRecorder(meterFactory);
        var middleware = Create(Policy(), new RateLimiterMetrics(meterFactory), new CapturingLogger<DistributedRateLimitingMiddleware>());

        await middleware.InvokeAsync(Request("/health"));

        Assert.Empty(recorder.For("ratelimiter.decisions"));
    }

    [Theory]
    [InlineData(FailureMode.FailOpen, "fail_open", LogLevel.Warning, 2001)]
    [InlineData(FailureMode.FailClosed, "fail_closed", LogLevel.Error, 2002)]
    public async Task LimiterFailure_IsCountedAndLoggedWithTheException(
        FailureMode mode, string expectedOutcome, LogLevel expectedLevel, int expectedEventId)
    {
        var meterFactory = TestMetrics.NewMeterFactory();
        using var recorder = new MetricsRecorder(meterFactory);
        var logger = new CapturingLogger<DistributedRateLimitingMiddleware>();
        var middleware = Create(Policy(mode), new RateLimiterMetrics(meterFactory), logger, new FailingFactory());

        await middleware.InvokeAsync(Request());

        Assert.Equal(expectedOutcome, recorder.For("ratelimiter.decisions").Single().Tags["outcome"]);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Equal(expectedEventId, entry.EventId.Id);
        Assert.Equal("orders", entry.Fields["Policy"]);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task Rejection_IsLoggedAtDebug_WithStructuredFields()
    {
        var logger = new CapturingLogger<DistributedRateLimitingMiddleware>();
        var middleware = Create(Policy(), TestMetrics.Create(), logger);

        await middleware.InvokeAsync(Request());
        await middleware.InvokeAsync(Request());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(1001, entry.EventId.Id);
        Assert.Equal("orders", entry.Fields["Policy"]);
        Assert.Equal("TokenBucket", entry.Fields["Algorithm"]);
        Assert.Equal("ip:203.0.113.7", entry.Fields["ClientId"]);
        Assert.True((int)entry.Fields["RetryAfterSeconds"]! >= 1);
    }
}
