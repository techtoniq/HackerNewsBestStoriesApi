using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BestStories.Api.IntegrationTests;

public sealed class ApiEndpointIntegrationTests
{
    [Fact]
    public async Task GivenValidStoryCount_WhenBestStoriesEndpointIsCalled_ThenStoriesAreReturnedAsJson()
    {
        // Given
        var provider = new StubStoryProvider([CreateStory("Highest", 90), CreateStory("Next", 70)]);
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync(
            "/api/stories/best?n=2",
            TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var stories = await response.Content.ReadFromJsonAsync<StoryResponse[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(stories);
        Assert.Equal(["Highest", "Next"], stories.Select(story => story.Title));
        Assert.Equal(2, provider.LastRequestedCount);
    }

    [Theory]
    [InlineData("/api/stories/best")]
    [InlineData("/api/stories/best?n=abc")]
    [InlineData("/api/stories/best?n=0")]
    [InlineData("/api/stories/best?n=501")]
    [InlineData("/api/stories/best?n=1&n=2")]
    public async Task GivenInvalidStoryCount_WhenBestStoriesEndpointIsCalled_ThenBadRequestProblemDetailsAreReturned(
        string path)
    {
        // Given
        using var factory = CreateFactory(new StubStoryProvider([]));
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GivenAnUnknownRoute_WhenTheApiIsCalled_ThenNotFoundProblemDetailsAreReturned()
    {
        // Given
        using var factory = CreateFactory(new StubStoryProvider([]));
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync("/not-a-route", TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenTheProviderIsUnavailable_WhenBestStoriesAreRequested_ThenServiceUnavailableProblemDetailsAreReturned()
    {
        // Given
        using var factory = CreateFactory(new StubStoryProvider(
            [],
            new StoryDataUnavailableException("private upstream details")));
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync(
            "/api/stories/best?n=1",
            TestContext.Current.CancellationToken);

        // Then
        await AssertProblemDetailsAsync(response, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GivenTheProviderThrowsUnexpectedly_WhenBestStoriesAreRequested_ThenGenericServerProblemDetailsAreReturned()
    {
        // Given
        const string privateMessage = "internal platform details";
        using var factory = CreateFactory(new StubStoryProvider([], new InvalidOperationException(privateMessage)));
        using var client = factory.CreateClient();

        // When
        using var response = await client.GetAsync(
            "/api/stories/best?n=1",
            TestContext.Current.CancellationToken);

        // Then
        var body = await AssertProblemDetailsAsync(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(privateMessage, body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Fact]
    public async Task GivenTheApplicationIsRunning_WhenHealthEndpointsAreCalled_ThenLivenessAndReadinessAreSuccessful()
    {
        // Given
        using var factory = CreateFactory(new StubStoryProvider([]));
        using var client = factory.CreateClient();

        // When
        using var liveness = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using var readiness = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal("application/json", readiness.Content.Headers.ContentType?.MediaType);
    }

    private static WebApplicationFactory<Program> CreateFactory(StubStoryProvider provider)
    {
        var cache = new StoryCache();
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["StoryCache:AutomaticRefresh"] = "false",
                        ["DistributedCache:Provider"] = "Memory"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IStoryProvider>();
                    services.AddSingleton<IStoryProvider>(provider);
                    services.RemoveAll<IStoryCache>();
                    services.AddSingleton<IStoryCache>(cache);
                });
            });
    }

    private static async Task<string> AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement;
        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("title", out _));
        Assert.True(problem.TryGetProperty("detail", out _));
        return body;
    }

    private static StoryResponse CreateStory(string title, int score) =>
        new(title, $"https://example.com/{title.ToLowerInvariant()}", "author", DateTimeOffset.UnixEpoch, score, 0);

    private sealed class StubStoryProvider(
        IReadOnlyList<StoryResponse> stories,
        Exception? exception = null) : IStoryProvider
    {
        public int? LastRequestedCount { get; private set; }

        public Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequestedCount = count;
            if (exception is not null)
            {
                return Task.FromException<IReadOnlyList<StoryResponse>>(exception);
            }

            return Task.FromResult<IReadOnlyList<StoryResponse>>(stories.Take(count).ToArray());
        }

        public Task RefreshAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
