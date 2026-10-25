namespace RateLimiter.Core.Redis;

/// <summary>
/// Loads the embedded .lua files once at startup. StackExchange.Redis caches each
/// script's SHA1 after the first call and sends EVALSHA afterwards, so the full
/// script text only crosses the network once per connection.
/// </summary>
internal static class LuaScripts
{
    public static readonly string FixedWindow = Load("fixed_window.lua");
    public static readonly string SlidingWindowCounter = Load("sliding_window_counter.lua");
    public static readonly string TokenBucket = Load("token_bucket.lua");

    private static string Load(string fileName)
    {
        var resourceName = $"RateLimiter.Core.Scripts.{fileName}";
        using var stream = typeof(LuaScripts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Lua script '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
