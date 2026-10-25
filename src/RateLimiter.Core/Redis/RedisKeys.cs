namespace RateLimiter.Core.Redis;

/// <summary>
/// Builds Redis keys. The "{clientId:route}" part is a Redis Cluster hash tag:
/// only the text inside the braces is hashed, so every key for one client+route
/// lands on the same shard. That matters for Sliding Window, whose script touches
/// two keys at once -- in a cluster, a multi-key script fails if its keys live on
/// different shards.
/// </summary>
internal static class RedisKeys
{
    private static string Tag(string clientId, string route) => $"{{{clientId}:{route}}}";

    public static string FixedWindow(string clientId, string route, long windowStartMs) =>
        $"ratelimit:fw:{Tag(clientId, route)}:{windowStartMs}";

    public static string SlidingWindow(string clientId, string route, long windowStartMs) =>
        $"ratelimit:sw:{Tag(clientId, route)}:{windowStartMs}";

    public static string TokenBucket(string clientId, string route) =>
        $"ratelimit:tb:{Tag(clientId, route)}";
}
