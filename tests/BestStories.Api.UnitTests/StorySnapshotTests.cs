using BestStories.Api.Domain;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;

namespace BestStories.Api.UnitTests;

public sealed class StorySnapshotTests
{
    [Fact]
    public void GivenUnorderedStories_WhenASnapshotIsCreated_ThenStoriesAreOrderedByScoreThenId()
    {
        // Given
        Story[] stories =
        [
            TestStories.Create(1, score: 30),
            TestStories.Create(2, score: 90),
            TestStories.Create(3, score: 30)
        ];

        // When
        var snapshot = StorySnapshot.Create(stories, DateTimeOffset.UnixEpoch);

        // Then
        Assert.Equal([2, 1, 3], snapshot.Stories.Select(story => story.Id));
    }

    [Fact]
    public void GivenASnapshotPublishedInTheFuture_WhenItsAgeIsMeasured_ThenTheAgeIsZero()
    {
        // Given
        var publishedAt = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var snapshot = StorySnapshot.Create([], publishedAt);

        // When
        var age = snapshot.AgeAt(DateTimeOffset.UnixEpoch);

        // Then
        Assert.Equal(TimeSpan.Zero, age);
    }

    [Fact]
    public async Task GivenAnEmptyInMemoryCache_WhenTheSnapshotIsRead_ThenNoSnapshotIsReturned()
    {
        // Given
        var cache = new InMemoryStoryCache();

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GivenAStoredSnapshot_WhenTheInMemoryCacheIsRead_ThenTheSameSnapshotIsReturned()
    {
        // Given
        var cache = new InMemoryStoryCache();
        var stored = TestStories.Snapshot(DateTimeOffset.UnixEpoch, 1, 2);
        await cache.SetSnapshotAsync(stored, TestContext.Current.CancellationToken);

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(stored, snapshot);
    }

    [Fact]
    public void GivenASnapshotFromCreate_WhenItIsValidated_ThenItIsValid()
    {
        // Given
        var snapshot = TestStories.Snapshot(DateTimeOffset.UnixEpoch, 3, 1, 2);

        // When
        var valid = StorySnapshot.IsValid(snapshot, out var problem);

        // Then
        Assert.True(valid);
        Assert.Null(problem);
    }

    [Fact]
    public void GivenDuplicateStoryIds_WhenTheSnapshotIsValidated_ThenItIsInvalid()
    {
        // Given
        var snapshot = new StorySnapshot([TestStories.Create(1, score: 5), TestStories.Create(1, score: 5)], DateTimeOffset.UnixEpoch);

        // When
        var valid = StorySnapshot.IsValid(snapshot, out var problem);

        // Then
        Assert.False(valid);
        Assert.NotNull(problem);
    }

    [Fact]
    public void GivenMoreStoriesThanTheMaximum_WhenTheSnapshotIsValidated_ThenItIsInvalid()
    {
        // Given
        var snapshot = TestStories.Snapshot(DateTimeOffset.UnixEpoch, [.. Enumerable.Range(1, StoryLimits.MaximumCount + 1)]);

        // When
        var valid = StorySnapshot.IsValid(snapshot, out _);

        // Then
        Assert.False(valid);
    }
}
