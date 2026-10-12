using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace BestStories.Api.ComponentTests;

public sealed class RedisStoryCacheTests
{
    [Fact]
    public async Task GivenASecondCacheReader_WhenAStorySnapshotIsStored_ThenTheSnapshotCanBeReadBack()
    {
        // Given
        using var services = new ServiceCollection()
            .AddDistributedMemoryCache()
            .BuildServiceProvider();
        var firstCache = new RedisStoryCache(services.GetRequiredService<IDistributedCache>());
        var secondCache = new RedisStoryCache(services.GetRequiredService<IDistributedCache>());
        var stories = new[]
        {
            CreateStory("Lower score", 30),
            CreateStory("Highest score", 90)
        };

        // When
        await firstCache.SetSnapshotAsync(stories, TestContext.Current.CancellationToken);
        var snapshot = await secondCache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(snapshot);
        Assert.Collection(
            snapshot.Stories,
            story => Assert.Equal("Highest score", story.Title),
            story => Assert.Equal("Lower score", story.Title));
    }

    private static StoryResponse CreateStory(string title, int score) =>
        new(title, "https://example.com", "author", DateTimeOffset.UnixEpoch, score, 0);
}
