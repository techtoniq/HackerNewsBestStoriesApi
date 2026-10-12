using BestStories.Api.ComponentTests.TestSupport;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using BestStories.Api.Infrastructure.Caching;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.ComponentTests;

public sealed class RedisStoryCacheTests(RedisFixture redis)
{
    private readonly string _instanceName = RedisFixture.NewInstanceName();
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public async Task GivenASnapshotStoredByOneInstance_WhenAnotherInstanceReadsIt_ThenTheSameSnapshotIsReturned()
    {
        // Given
        var writer = CreateCache();
        var reader = CreateCache();
        var stored = CreateSnapshot(DateTimeOffset.UnixEpoch, (1, 30), (2, 90));

        // When
        await writer.SetSnapshotAsync(stored, TestContext.Current.CancellationToken);
        var snapshot = await reader.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(snapshot);
        Assert.Equal(stored.PublishedAt, snapshot.PublishedAt);
        Assert.Equal(stored.Stories, snapshot.Stories);
    }

    [Fact]
    public async Task GivenNoSnapshot_WhenTheCacheIsRead_ThenNoSnapshotIsReturned()
    {
        // Given
        var cache = CreateCache();

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GivenALocalCopy_WhenAnotherInstancePublishes_ThenTheReaderSeesItOnceTheLocalCopyExpires()
    {
        // Given
        var writer = CreateCache();
        var reader = CreateCache(localCacheMilliseconds: 1000);
        await writer.SetSnapshotAsync(CreateSnapshot(DateTimeOffset.UnixEpoch, (1, 10)), TestContext.Current.CancellationToken);
        var first = await reader.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var newer = CreateSnapshot(DateTimeOffset.UnixEpoch.AddMinutes(5), (2, 20));
        await writer.SetSnapshotAsync(newer, TestContext.Current.CancellationToken);

        // When
        var withinLocalWindow = await reader.GetSnapshotAsync(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var afterLocalWindow = await reader.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(first, withinLocalWindow);
        Assert.NotNull(afterLocalWindow);
        Assert.Equal(newer.PublishedAt, afterLocalWindow.PublishedAt);
    }

    [Fact]
    public async Task GivenACorruptSharedSnapshot_WhenTheCacheIsRead_ThenInvalidDataIsReported()
    {
        // Given
        await CreateDistributedCache().SetAsync(
            RedisStoryCache.SnapshotKey,
            "not json"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var cache = CreateCache();

        // When
        var exception = await Record.ExceptionAsync(
            () => cache.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    private RedisCache CreateDistributedCache() =>
        new(Options.Create(new RedisCacheOptions
        {
            InstanceName = _instanceName,
            ConnectionMultiplexerFactory = () => Task.FromResult(redis.Connection)
        }));

    private RedisStoryCache CreateCache(int localCacheMilliseconds = 0) =>
        new(
            CreateDistributedCache(),
            Options.Create(new DistributedCacheOptions
            {
                InstanceName = _instanceName,
                LocalCacheMilliseconds = localCacheMilliseconds
            }),
            _timeProvider);

    private static StorySnapshot CreateSnapshot(DateTimeOffset publishedAt, params (int Id, int Score)[] stories) =>
        StorySnapshot.Create(
            stories.Select(story => new Story(
                story.Id,
                $"Story {story.Id}",
                $"https://example.com/{story.Id}",
                "author",
                DateTimeOffset.UnixEpoch,
                story.Score,
                3)),
            publishedAt);
}
