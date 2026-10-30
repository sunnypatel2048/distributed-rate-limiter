using Microsoft.Extensions.Logging;

namespace RateLimiter.Middleware;

/// <summary>
/// Structured, source-generated log messages. Named placeholders ({Policy}, {ClientId})
/// become queryable fields in a JSON log pipeline instead of text baked into a string,
/// and the generator skips all formatting work when the level is disabled -- which
/// matters on a path that runs for every request.
/// </summary>
internal static partial class RateLimiterLog
{
    // Debug, not Information: at thousands of requests per second, logging every 429
    // would flood the logs. The rejected-count metric is the right tool for volume;
    // turn this on when investigating one specific client.
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Rate limited client {ClientId} on policy {Policy} ({Algorithm}); retry after {RetryAfterSeconds}s")]
    public static partial void RequestRejected(
        ILogger logger, string clientId, string policy, string algorithm, int retryAfterSeconds);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Rate limiter unavailable for policy {Policy}; failing open (request allowed without a limit check)")]
    public static partial void FailingOpen(ILogger logger, Exception exception, string policy);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "Rate limiter unavailable for policy {Policy}; failing closed (request rejected with 503)")]
    public static partial void FailingClosed(ILogger logger, Exception exception, string policy);
}
