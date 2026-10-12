using BestStories.Api.Models;
using BestStories.Api.Services;

namespace BestStories.Api.UnitTests;

public sealed class StoryCacheTests
{
    [Fact]
    public async Task GivenStoriesWithDifferentScores_WhenBestStoriesAreRequested_ThenResultsAreScoreSorted()
    {
        // Given
        var cache = new StoryCache();
        await cache.SetSnapshotAsync(
        [
            CreateStory("Lower score", 30),
            CreateStory("Highest score", 90),
            CreateStory("Middle score", 60)
        ], TestContext.Current.CancellationToken);

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var stories = snapshot?.Stories.Take(2).ToArray();

        // Then
        Assert.NotNull(stories);
        Assert.Collection(
            stories,
            story => Assert.Equal("Highest score", story.Title),
            story => Assert.Equal("Middle score", story.Title));
    }

    [Fact]
    public async Task GivenAnEmptyCache_WhenBestStoriesAreRequested_ThenNoStoriesAreReturned()
    {
        // Given
        var cache = new StoryCache();

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GivenFewerCachedStoriesThanRequested_WhenBestStoriesAreRequested_ThenAvailableStoriesAreReturned()
    {
        // Given
        var cache = new StoryCache();
        await cache.SetSnapshotAsync(
            [CreateStory("Only story", 10)],
            TestContext.Current.CancellationToken);

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var stories = snapshot?.Stories.Take(5).ToArray();

        // Then
        Assert.NotNull(stories);
        Assert.Single(stories);
    }

    private static StoryResponse CreateStory(string title, int score) =>
        new(title, "https://example.com", "author", DateTimeOffset.UnixEpoch, score, 0);
}
