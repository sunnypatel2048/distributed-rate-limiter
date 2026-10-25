using System.Net;

namespace RateLimiter.Middleware.Tests.EndToEnd;

/// <summary>
/// Full HTTP round trips through the demo app against a real Redis container.
/// Every test uses its own random API key, so tests never share quota.
/// (TestServer doesn't set a client IP, so the API key is also what identifies the client.)
/// </summary>
[Trait("Category", "Integration")]
public class DemoApiEndToEndTests(RedisContainerFixture redis) : IClassFixture<RedisContainerFixture>
{
    private static string NewApiKey() => Guid.NewGuid().ToString("N");

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", apiKey);
        return client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : string.Empty;

    [Fact]
    public async Task OrdersPolicy_AllowsTenRequests_ThenReturns429WithRetryAfter()
    {
        using var factory = new DemoAppFactory(redis.ConnectionString);
        using var client = factory.CreateClient();
        var apiKey = NewApiKey();

        for (var i = 0; i < 10; i++)
        {
            var allowed = await Get(client, "/api/orders", apiKey);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("10", Header(allowed, "X-RateLimit-Limit"));
        }

        var denied = await Get(client, "/api/orders", apiKey);

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal("0", Header(denied, "X-RateLimit-Remaining"));
        var retryAfter = denied.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6));
        Assert.Contains("rate_limited", await denied.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/orders/42", "10")]   // "orders" (shares the /api/orders quota)
    [InlineData("/api/search?q=redis", "20")]
    [InlineData("/api/products", "100")]   // falls through to "api-default"
    public async Task RequestsAreRoutedToTheLongestMatchingPolicy(string path, string expectedLimit)
    {
        using var factory = new DemoAppFactory(redis.ConnectionString);
        using var client = factory.CreateClient();

        var response = await Get(client, path, NewApiKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedLimit, Header(response, "X-RateLimit-Limit"));
    }

    [Fact]
    public async Task HealthEndpoint_IsNeverRateLimited()
    {
        using var factory = new DemoAppFactory(redis.ConnectionString);
        using var client = factory.CreateClient();
        var apiKey = NewApiKey();

        for (var i = 0; i < 30; i++)
        {
            var response = await Get(client, "/health", apiKey);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("X-RateLimit-Limit"));
        }
    }

    [Fact]
    public async Task DifferentApiKeys_HaveSeparateQuotas()
    {
        using var factory = new DemoAppFactory(redis.ConnectionString);
        using var client = factory.CreateClient();
        var exhaustedKey = NewApiKey();

        for (var i = 0; i < 11; i++)
        {
            await Get(client, "/api/orders", exhaustedKey);
        }

        var otherClient = await Get(client, "/api/orders", NewApiKey());

        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task TwoAppInstancesSharingRedis_EnforceOneCombinedLimit()
    {
        // The whole point of the project, over real HTTP: two independent app instances
        // (separate middleware, separate Redis connections) behind an imaginary load
        // balancer still admit exactly 10 orders requests for one client in total.
        using var instanceA = new DemoAppFactory(redis.ConnectionString);
        using var instanceB = new DemoAppFactory(redis.ConnectionString);
        using var clientA = instanceA.CreateClient();
        using var clientB = instanceB.CreateClient();
        var apiKey = NewApiKey();

        var requests = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Get(clientA, "/api/orders", apiKey),
            Get(clientB, "/api/orders", apiKey)
        });
        var responses = await Task.WhenAll(requests);

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(30, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }
}
