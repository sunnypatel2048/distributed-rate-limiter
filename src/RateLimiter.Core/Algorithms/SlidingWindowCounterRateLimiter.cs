using System.Collections.Concurrent;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// In-memory Sliding Window Counter. Estimates the request count by weighting the
/// previous window's count by how much of the current window remains unelapsed —
/// this is what smooths out the boundary-burst problem Fixed Window has.
///
/// Same design choice as Fixed Window: only allowed requests increment the counter.
/// </summary>
public sealed class SlidingWindowCounterRateLimiter(TimeProvider timeProvider, RateLimiterOptions options) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindowState> _store = new();

    private sealed record SlidingWindowState(DateTimeOffset CurrentWindowStart, int CurrentCount, int PreviousCount);

    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        var key = $"{clientId}:{route}";
        var now = timeProvider.GetUtcNow();
        var windowTicks = options.Window.Ticks;
        var currentWindowStart = new DateTimeOffset((now.UtcTicks / windowTicks) * windowTicks, TimeSpan.Zero);

        while (true)
        {
            var oldState = _store.GetOrAdd(key, new SlidingWindowState(currentWindowStart, 0, 0));

            // Figure out what "previous" and "current" mean relative to now:
            //  - same window as last time  -> state carries over as-is
            //  - exactly one window later   -> last window's current becomes this window's previous
            //  - more than one window later -> client was idle, previous window had zero requests
            var baseState = oldState.CurrentWindowStart == currentWindowStart
                ? oldState
                : oldState.CurrentWindowStart + options.Window == currentWindowStart
                    ? new SlidingWindowState(currentWindowStart, 0, oldState.CurrentCount)
                    : new SlidingWindowState(currentWindowStart, 0, 0);

            // Weight = fraction of the current window still "remaining" — this is what
            // makes the previous window's influence fade out as time passes.
            var elapsed = now - currentWindowStart;
            var weight = Math.Clamp(1.0 - (elapsed.Ticks / (double)windowTicks), 0.0, 1.0);
            var estimatedCount = baseState.CurrentCount + (baseState.PreviousCount * weight);

            var allowed = estimatedCount < options.PermitLimit;
            var newState = allowed
                ? baseState with { CurrentCount = baseState.CurrentCount + 1 }
                : baseState;

            if (_store.TryUpdate(key, newState, oldState))
            {
                if (allowed)
                {
                    var remaining = (int)Math.Max(0, options.PermitLimit - (estimatedCount + 1));
                    return Task.FromResult(RateLimitResult.Allowed(options.PermitLimit, remaining));
                }

                // Window-end is a conservative (safe upper bound) Retry-After — the true
                // moment weight decays enough to allow again is earlier and continuous.
                var windowEnd = currentWindowStart + options.Window;
                return Task.FromResult(RateLimitResult.Denied(options.PermitLimit, windowEnd - now));
            }
        }
    }
}
