using System.Text;
using BestStories.Api.Configuration;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

/// <summary>
/// Outage and payload handling of the Redis story cache. Round trips against a real Redis run in
/// the component tests; these use <see cref="FakeDistributedCache"/> to fail or stall on demand.
/// </summary>
public sealed class RedisStoryCacheTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeDistributedCache _distributedCache = new();

    [Fact]
    public async Task GivenALocalCopy_WhenRedisFails_ThenTheLocalCopyIsServed()
    {
        // Given
        var cache = CreateCache();
        var snapshot = TestStories.Snapshot(_timeProvider.GetUtcNow(), 1, 2);
        await cache.SetSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        _distributedCache.ReadFailure = new TimeoutException("Simulated RedisTimeoutException.");

        // When
        var served = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(snapshot, served);
        Assert.Equal(1, _distributedCache.Reads);
    }

    [Fact]
    public async Task GivenNoLocalCopy_WhenRedisFails_ThenTheFailureIsReported()
    {
        // Given
        var cache = CreateCache();
        _distributedCache.ReadFailure = new TimeoutException("Simulated RedisTimeoutException.");

        // When
        var exception = await Record.ExceptionAsync(
            () => cache.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<TimeoutException>(exception);
    }

    [Fact]
    public async Task GivenALocalCopy_WhenRedisStalls_ThenTheLocalCopyIsServedAfterTheReadTimeout()
    {
        // Given
        var cache = CreateCache();
        var snapshot = TestStories.Snapshot(_timeProvider.GetUtcNow(), 1);
        await cache.SetSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var stall = new TaskCompletionSource();
        _distributedCache.ReadGate = stall.Task;

        // When
        var read = cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(
            () => read.IsCompleted,
            beforeEachCheck: () => _timeProvider.Advance(TimeSpan.FromMilliseconds(250)));
        var served = await read;
        stall.SetResult();

        // Then
        Assert.Same(snapshot, served);
    }

    [Fact]
    public async Task GivenARecentRedisFailure_WhenReadsArriveDuringTheBackoff_ThenRedisIsNotTriedAgain()
    {
        // Given
        var cache = CreateCache();
        await cache.SetSnapshotAsync(TestStories.Snapshot(_timeProvider.GetUtcNow(), 1), TestContext.Current.CancellationToken);
        _distributedCache.ReadFailure = new TimeoutException("Simulated RedisTimeoutException.");
        await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // When
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var readsDuringBackoff = _distributedCache.Reads;
        _timeProvider.Advance(TimeSpan.FromSeconds(4));
        await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, readsDuringBackoff);
        Assert.Equal(2, _distributedCache.Reads);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"stories":null,"publishedAt":"2026-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"stories":[{"id":1,"title":null,"url":"https://example.com","postedBy":"a","postedAt":"2026-01-01T00:00:00+00:00","score":1,"commentCount":0}],"publishedAt":"2026-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"stories":[{"id":1,"title":"A","url":"https://example.com","postedBy":"a","postedAt":"2026-01-01T00:00:00+00:00","score":1,"commentCount":0},{"id":2,"title":"B","url":"https://example.com","postedBy":"a","postedAt":"2026-01-01T00:00:00+00:00","score":5,"commentCount":0}],"publishedAt":"2026-01-01T00:00:00+00:00"}""")]
    public async Task GivenAnInvalidSharedPayload_WhenTheCacheIsRead_ThenItIsTreatedAsMissing(string payload)
    {
        // Given
        _distributedCache.SetRaw(RedisStoryCache.SnapshotKey, Encoding.UTF8.GetBytes(payload));
        var cache = CreateCache();

        // When
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GivenAValidSharedPayload_WhenTheCacheIsRead_ThenItIsReturned()
    {
        // Given
        var writer = CreateCache();
        var stored = TestStories.Snapshot(_timeProvider.GetUtcNow(), 1, 2, 3);
        await writer.SetSnapshotAsync(stored, TestContext.Current.CancellationToken);
        var reader = CreateCache();

        // When
        var snapshot = await reader.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(snapshot);
        Assert.Equal(stored.Stories, snapshot.Stories);
    }

    private RedisStoryCache CreateCache() =>
        new(
            _distributedCache,
            Options.Create(new DistributedCacheOptions
            {
                LocalCacheMilliseconds = 0,
                ReadTimeoutMilliseconds = 1000,
                ReadFailureBackoffMilliseconds = 5000
            }),
            _timeProvider,
            NullLogger<RedisStoryCache>.Instance);
}
