using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BestStories.Api.Application;
using BestStories.Api.Domain;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BestStories.Api.IntegrationTests;

public sealed class ApiEndpointIntegrationTests
{
    [Fact]
    public async Task GivenUpstreamStories_WhenBestStoriesAreRequested_ThenTheyAreReturnedByScoreInTheContractFormat()
    {
        // Given
        await using var factory = new BestStoriesApiFactory();
        factory.HackerNews.BestStoryIds = [1, 2, 3, 4];
        factory.HackerNews.AddStory(1, "Lowest", 10, url: "https://example.com/1", descendants: 5);
        factory.HackerNews.AddStory(2, "Highest", 30);
        factory.HackerNews.AddDeletedStory(3);
        factory.HackerNews.AddStory(4, "Middle", 20, url: "https://example.com/4", descendants: 7);
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync("/api/stories/best?n=3", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = await ReadJsonAsync(response);
        var stories = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(["Highest", "Middle", "Lowest"], stories.Select(story => story.GetProperty("title").GetString()));

        var highest = stories[0];
        Assert.Equal("https://news.ycombinator.com/item?id=2", highest.GetProperty("uri").GetString());
        Assert.Equal("author", highest.GetProperty("postedBy").GetString());
        Assert.Equal("2019-10-12T13:43:01+00:00", highest.GetProperty("time").GetString());
        Assert.Equal(30, highest.GetProperty("score").GetInt32());
        Assert.Equal(0, highest.GetProperty("commentCount").GetInt32());
        Assert.Equal(7, stories[1].GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task GivenACachedSnapshot_WhenStoriesAreRequestedAgain_ThenHackerNewsIsNotCalledAgain()
    {
        // Given
        await using var factory = CreateFactoryWithStories(storyCount: 5);
        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/api/stories/best?n=5", TestContext.Current.CancellationToken);

        // When
        using var second = await client.GetAsync("/api/stories/best?n=2", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var document = await ReadJsonAsync(second);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal(1, factory.HackerNews.ListRequests);
    }

    [Fact]
    public async Task GivenASuccessfulResponse_WhenItIsRequestedWithItsETag_ThenNotModifiedIsReturned()
    {
        // Given
        await using var factory = CreateFactoryWithStories(storyCount: 3);
        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/api/stories/best?n=3", TestContext.Current.CancellationToken);
        var entityTag = first.Headers.ETag;

        // When
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=3");
        request.Headers.IfNoneMatch.Add(entityTag!);
        using var second = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(entityTag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.Public);
        Assert.InRange(first.Headers.CacheControl!.MaxAge!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.NotNull(first.Content.Headers.LastModified);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GivenADifferentCount_WhenItIsRequested_ThenTheETagDiffers()
    {
        // Given
        await using var factory = CreateFactoryWithStories(storyCount: 3);
        using var client = factory.CreateClient();

        // When
        using var first = await client.GetAsync("/api/stories/best?n=3", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/api/stories/best?n=2", TestContext.Current.CancellationToken);

        // Then
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
    }

    [Theory]
    [InlineData("/api/stories/best")]
    [InlineData("/api/stories/best?n=abc")]
    [InlineData("/api/stories/best?n=0")]
    [InlineData("/api/stories/best?n=501")]
    [InlineData("/api/stories/best?n=1&n=2")]
    public async Task GivenInvalidStoryCount_WhenBestStoriesAreRequested_ThenBadRequestProblemDetailsAreReturned(string path)
    {
        // Given
        await using var factory = new BestStoriesApiFactory();
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, factory.HackerNews.ListRequests);
    }

    [Fact]
    public async Task GivenAnUnknownRoute_WhenTheApiIsCalled_ThenNotFoundProblemDetailsAreReturned()
    {
        // Given
        await using var factory = new BestStoriesApiFactory();
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync("/not-a-route", TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenTheRateLimitIsExceeded_WhenBestStoriesAreRequested_ThenTooManyRequestsIsReturned()
    {
        // Given
        await using var factory = CreateFactoryWithStories(
            storyCount: 1,
            new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = "1",
                ["RateLimiting:WindowSeconds"] = "60"
            });
        using var client = factory.CreateClient();
        using var allowed = await client.GetAsync("/api/stories/best?n=1", TestContext.Current.CancellationToken);

        // When
        using var rejected = await client.GetAsync("/api/stories/best?n=1", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await AssertProblemDetailsAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Fact]
    public async Task GivenAutomaticRefreshBeforeTheFirstSnapshot_WhenTheApiIsCalled_ThenItIsUnavailableAndNotReady()
    {
        // Given
        var neverCompletes = new TaskCompletionSource();
        await using var factory = new BestStoriesApiFactory(new Dictionary<string, string?>
        {
            ["StoryCache:AutomaticRefresh"] = "true"
        });
        factory.HackerNews.Gate = neverCompletes.Task;
        using var client = factory.CreateClient();

        // When
        using var stories = await client.GetAsync("/api/stories/best?n=1", TestContext.Current.CancellationToken);
        using var readiness = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var liveness = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(stories, HttpStatusCode.ServiceUnavailable);
        Assert.NotNull(stories.Headers.RetryAfter);
        await AssertProblemDetailsAsync(readiness, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task GivenAutomaticRefreshWithASnapshotBeyondMaxStaleness_WhenTheApiIsCalled_ThenStoriesAreUnavailableButTheInstanceStaysReady()
    {
        // Given
        var neverCompletes = new TaskCompletionSource();
        var cache = new InMemoryStoryCache();
        await cache.SetSnapshotAsync(
            StorySnapshot.Create(
                [new Story(1, "Old", "https://example.com/1", "author", DateTimeOffset.UnixEpoch, 10, 0)],
                DateTimeOffset.UtcNow.AddHours(-2)),
            TestContext.Current.CancellationToken);
        await using var factory = new BestStoriesApiFactory(
            new Dictionary<string, string?> { ["StoryCache:AutomaticRefresh"] = "true" },
            services =>
            {
                services.RemoveAll<IStoryCache>();
                services.AddSingleton<IStoryCache>(cache);
            });
        factory.HackerNews.Gate = neverCompletes.Task;
        using var client = factory.CreateClient();

        // When
        using var stories = await client.GetAsync("/api/stories/best?n=1", TestContext.Current.CancellationToken);
        using var readiness = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(stories, HttpStatusCode.ServiceUnavailable);
        Assert.NotNull(stories.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        using var document = await ReadJsonAsync(readiness);
        Assert.Equal("degraded", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GivenATrustedProxy_WhenClientsBehindItExceedTheRateLimit_ThenEachClientIsLimitedSeparately()
    {
        // Given
        await using var factory = CreateFactoryWithStories(
            storyCount: 1,
            new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = "1",
                ["RateLimiting:WindowSeconds"] = "60",
                ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.1"
            },
            services => services.AddSingleton<IStartupFilter>(
                new RemoteAddressStartupFilter(IPAddress.Parse("10.0.0.1"))));
        using var client = factory.CreateClient();

        // When
        using var firstClient = await GetStoriesForwardedForAsync(client, "203.0.113.1");
        using var secondClient = await GetStoriesForwardedForAsync(client, "203.0.113.2");
        using var firstClientAgain = await GetStoriesForwardedForAsync(client, "203.0.113.1");

        // Then
        Assert.Equal(HttpStatusCode.OK, firstClient.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondClient.StatusCode);
        await AssertProblemDetailsAsync(firstClientAgain, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GivenAnUntrustedSender_WhenItSendsForwardedHeaders_ThenTheyAreIgnoredForRateLimiting()
    {
        // Given
        await using var factory = CreateFactoryWithStories(
            storyCount: 1,
            new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = "1",
                ["RateLimiting:WindowSeconds"] = "60"
            },
            services => services.AddSingleton<IStartupFilter>(
                new RemoteAddressStartupFilter(IPAddress.Parse("198.51.100.9"))));
        using var client = factory.CreateClient();

        // When
        using var first = await GetStoriesForwardedForAsync(client, "203.0.113.1");
        using var spoofed = await GetStoriesForwardedForAsync(client, "203.0.113.2");

        // Then
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemDetailsAsync(spoofed, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GivenOnDemandRefresh_WhenHealthEndpointsAreCalled_ThenLivenessAndReadinessAreSuccessful()
    {
        // Given
        await using var factory = new BestStoriesApiFactory();
        using var client = factory.CreateClient();

        // When
        using var liveness = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using var readiness = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal("application/json", readiness.Content.Headers.ContentType?.MediaType);
        Assert.True(readiness.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GivenTheProviderThrowsUnexpectedly_WhenBestStoriesAreRequested_ThenGenericServerProblemDetailsAreReturned()
    {
        // Given
        const string privateMessage = "internal platform details";
        await using var factory = new BestStoriesApiFactory(configureServices: services =>
        {
            services.RemoveAll<IStoryProvider>();
            services.AddSingleton<IStoryProvider>(new ThrowingStoryProvider(new InvalidOperationException(privateMessage)));
        });
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync("/api/stories/best?n=1", TestContext.Current.CancellationToken);

        // Then
        var body = await AssertProblemDetailsAsync(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(privateMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenTheApplicationIsRunning_WhenTheOpenApiDocumentIsRequested_ThenItDescribesTheStoriesEndpoint()
    {
        // Given
        await using var factory = new BestStoriesApiFactory();
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("/api/stories/best", body, StringComparison.Ordinal);
    }

    private static BestStoriesApiFactory CreateFactoryWithStories(
        int storyCount,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var factory = new BestStoriesApiFactory(settings, configureServices);
        factory.HackerNews.BestStoryIds = Enumerable.Range(1, storyCount).ToArray();
        foreach (var id in factory.HackerNews.BestStoryIds)
        {
            factory.HackerNews.AddStory(id, $"Story {id}", score: id * 10);
        }

        return factory;
    }

    private static async Task<HttpResponseMessage> GetStoriesForwardedForAsync(HttpClient client, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=1");
        request.Headers.Add("X-Forwarded-For", clientAddress);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string> AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Server"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement;
        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("title", out _));
        Assert.True(problem.TryGetProperty("detail", out _));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        return body;
    }

    private sealed class ThrowingStoryProvider(Exception exception) : IStoryProvider
    {
        public Task<StorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<StorySnapshot>(exception);

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
