using System.Collections.Concurrent;

namespace RateLimiter.Core.Algorithms;

/// <summary>
/// In-memory Fixed Window algorithm.
///
/// Design choice: the counter only increments on an ALLOWED request. Denied requests
/// don't consume quota. This keeps "Count" meaning "admitted requests this window",
/// which keeps the CAS logic below simple to reason about.
/// </summary>
public sealed class FixedWindowRateLimiter(TimeProvider timeProvider, RateLimiterOptions options) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, FixedWindowState> _store = new();

    private sealed record FixedWindowState(DateTimeOffset WindowStart, int Count);

    public Task<RateLimitResult> CheckAsync(
        string clientId,
        string route,
        CancellationToken cancellationToken = default)
    {
        var key = $"{clientId}:{route}";
        var now = timeProvider.GetUtcNow();
        var windowTicks = options.Window.Ticks;

        // Floor "now" to the start of the current fixed window.
        var currentWindowStart = new DateTimeOffset((now.UtcTicks / windowTicks) * windowTicks, TimeSpan.Zero);

        while (true)
        {
            var oldState = _store.GetOrAdd(key, new FixedWindowState(currentWindowStart, 0));

            // If the stored window doesn't match the current one, it has rolled over — reset.
            var baseCount = oldState.WindowStart == currentWindowStart ? oldState.Count : 0;
            var allowed = baseCount < options.PermitLimit;

            var newState = allowed
                ? new FixedWindowState(currentWindowStart, baseCount + 1)
                : new FixedWindowState(currentWindowStart, baseCount);

            // Atomic compare-and-swap: if another thread updated the entry since we read
            // it, TryUpdate fails, and we retry from scratch with the fresh value.
            // This is the single-process rehearsal for the exact race condition that
            // Wednesday's Redis Lua script solves across multiple processes.
            if (_store.TryUpdate(key, newState, oldState))
            {
                if (allowed)
                {
                    var remaining = options.PermitLimit - newState.Count;
                    return Task.FromResult(RateLimitResult.Allowed(options.PermitLimit, remaining));
                }

                var windowEnd = currentWindowStart + options.Window;
                return Task.FromResult(RateLimitResult.Denied(options.PermitLimit, windowEnd - now));
            }
        }
    }
}
