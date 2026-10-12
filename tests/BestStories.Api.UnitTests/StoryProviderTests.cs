using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

public sealed class StoryProviderTests : IDisposable
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly InMemoryStoryCache _cache = new();
    private readonly FakeHackerNewsClient _client = new();
    private readonly TestHostApplicationLifetime _lifetime = new();
    private readonly DateTimeOffset _start;

    public StoryProviderTests() => _start = _timeProvider.GetUtcNow();

    public void Dispose() => _lifetime.Dispose();

    [Fact]
    public async Task GivenNoSnapshotOnDemand_WhenStoriesAreRequested_ThenTheCacheIsLoadedAndReturned()
    {
        // Given
        var provider = CreateProvider();

        // When
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([1], snapshot.Stories.Select(story => story.Id));
        Assert.Equal(1, _client.ListRequests);
        Assert.Same(snapshot, await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GivenConcurrentOnDemandCacheMisses_WhenStoriesAreRequested_ThenOneRefreshServesBothRequests()
    {
        // Given
        var provider = CreateProvider();
        _client.HoldListResponses();

        // When
        var firstRequest = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        var secondRequest = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        _client.ReleaseListResponses();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        // Then
        Assert.Equal(1, _client.ListRequests);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task GivenAFreshSnapshot_WhenStoriesAreRequested_ThenNoRefreshIsStarted()
    {
        // Given
        var existing = await PublishAsync(age: TimeSpan.FromSeconds(30));
        var provider = CreateProvider(settings => settings.CacheTtlSeconds = 60);

        // When
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(existing, snapshot);
        Assert.Equal(0, _client.ListRequests);
    }

    [Fact]
    public async Task GivenAStaleSnapshotWithinMaxStalenessOnDemand_WhenStoriesAreRequested_ThenItIsServedAndRefreshedInTheBackground()
    {
        // Given
        var existing = await PublishAsync(age: TimeSpan.FromSeconds(120));
        var provider = CreateProvider(settings =>
        {
            settings.CacheTtlSeconds = 60;
            settings.MaxStalenessSeconds = 600;
        });
        _client.HoldListResponses();

        // When
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(existing, snapshot);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        _client.ReleaseListResponses();
        await Eventually.HoldsAsync(async () =>
            await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken) is { } refreshed &&
            refreshed.PublishedAt > existing.PublishedAt);
    }

    [Fact]
    public async Task GivenASnapshotBeyondMaxStalenessOnDemand_WhenStoriesAreRequested_ThenTheRequestWaitsForARefresh()
    {
        // Given
        var existing = await PublishAsync(age: TimeSpan.FromSeconds(700));
        var provider = CreateProvider(settings => settings.MaxStalenessSeconds = 600);

        // When
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotSame(existing, snapshot);
        Assert.Equal(_timeProvider.GetUtcNow(), snapshot.PublishedAt);
        Assert.Equal(1, _client.ListRequests);
    }

    [Fact]
    public async Task GivenAStaleSnapshotWithinMaxStalenessInAutomaticMode_WhenStoriesAreRequested_ThenItIsServedWithoutARefresh()
    {
        // Given
        var existing = await PublishAsync(age: TimeSpan.FromSeconds(400));
        var provider = CreateProvider(settings =>
        {
            settings.AutomaticRefresh = true;
            settings.RefreshIntervalSeconds = 300;
            settings.MaxStalenessSeconds = 1800;
        });

        // When
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Same(existing, snapshot);
        Assert.Equal(0, _client.ListRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenNoAcceptableSnapshotInAutomaticMode_WhenStoriesAreRequested_ThenDataIsUnavailableWithoutARefresh(
        bool hasExpiredSnapshot)
    {
        // Given
        if (hasExpiredSnapshot)
        {
            await PublishAsync(age: TimeSpan.FromSeconds(2000));
        }

        var provider = CreateProvider(settings =>
        {
            settings.AutomaticRefresh = true;
            settings.MaxStalenessSeconds = 1800;
        });

        // When
        var exception = await Record.ExceptionAsync(
            () => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.Equal(0, _client.ListRequests);
    }

    [Fact]
    public async Task GivenARecentFailedRefresh_WhenStoriesAreRequestedDuringTheCooldown_ThenTheyFailWithoutCallingUpstream()
    {
        // Given
        var provider = CreateProvider(settings => settings.FailureCooldownSeconds = 30);
        _client.ListFailure = new HttpRequestException("Upstream failure.");
        await Assert.ThrowsAsync<StoryDataUnavailableException>(
            () => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // When
        _timeProvider.Advance(TimeSpan.FromSeconds(10));
        var exception = await Record.ExceptionAsync(
            () => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.Equal(1, _client.ListRequests);
    }

    [Fact]
    public async Task GivenACooldownThatHasExpired_WhenStoriesAreRequested_ThenUpstreamIsTriedAgain()
    {
        // Given
        var provider = CreateProvider(settings => settings.FailureCooldownSeconds = 30);
        _client.ListFailure = new HttpRequestException("Upstream failure.");
        await Assert.ThrowsAsync<StoryDataUnavailableException>(
            () => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
        _client.ListFailure = null;

        // When
        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotEmpty(snapshot.Stories);
        Assert.Equal(2, _client.ListRequests);
    }

    [Fact]
    public async Task GivenAnExistingSnapshotAndAnUpstreamFailure_WhenAScheduledRefreshRuns_ThenTheSnapshotIsRetained()
    {
        // Given
        var existing = await PublishAsync(age: TimeSpan.FromSeconds(400));
        var provider = CreateProvider(settings => settings.AutomaticRefresh = true);
        _client.ListFailure = new InvalidOperationException("Upstream failure.");

        // When
        var exception = await Assert.ThrowsAsync<StoryDataUnavailableException>(
            () => provider.RefreshAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Same(existing, await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GivenAnotherInstancePublishedWithinTheInterval_WhenAScheduledRefreshRuns_ThenItIsSkipped()
    {
        // Given
        await PublishAsync(age: TimeSpan.FromSeconds(100));
        var provider = CreateProvider(settings =>
        {
            settings.AutomaticRefresh = true;
            settings.RefreshIntervalSeconds = 300;
        });

        // When
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(0, _client.ListRequests);
    }

    [Fact]
    public async Task GivenTheSnapshotIsDueForRefresh_WhenAScheduledRefreshRuns_ThenUpstreamIsFetched()
    {
        // Given
        await PublishAsync(age: TimeSpan.FromSeconds(290));
        var provider = CreateProvider(settings =>
        {
            settings.AutomaticRefresh = true;
            settings.RefreshIntervalSeconds = 300;
        });

        // When
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, _client.ListRequests);
    }

    [Fact]
    public async Task GivenTwoInstancesSharingALock_WhenBothCachesMiss_ThenOnlyOneInstanceFetchesUpstream()
    {
        // Given
        var sharedLock = new InMemoryRefreshLock();
        var secondClient = new FakeHackerNewsClient();
        var first = CreateProvider(refreshLock: sharedLock, timeProvider: TimeProvider.System);
        var second = CreateProvider(
            refreshLock: sharedLock,
            timeProvider: TimeProvider.System,
            client: secondClient);
        _client.HoldListResponses();

        // When
        var firstRequest = first.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        var secondRequest = second.GetSnapshotAsync(TestContext.Current.CancellationToken);
        _client.ReleaseListResponses();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        // Then
        Assert.Equal(1, _client.ListRequests);
        Assert.Equal(0, secondClient.ListRequests);
        Assert.Equal(results[0].PublishedAt, results[1].PublishedAt);
    }

    [Fact]
    public async Task GivenAnotherInstanceHoldsTheLockTooLong_WhenStoriesAreRequested_ThenDataIsUnavailable()
    {
        // Given
        var sharedLock = new InMemoryRefreshLock();
        await using var heldElsewhere = await sharedLock.TryAcquireAsync(TestContext.Current.CancellationToken);
        var provider = CreateProvider(
            refreshLock: sharedLock,
            timeProvider: TimeProvider.System,
            configureLock: settings => settings.LockWaitSeconds = 1);

        // When
        var exception = await Record.ExceptionAsync(
            () => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.NotNull(heldElsewhere);
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.Equal(0, _client.ListRequests);
    }

    [Fact]
    public async Task GivenAnUpstreamThatIsSlowButNotFailing_WhenARefreshRuns_ThenItIsAbandonedAtTheRefreshTimeout()
    {
        // Given
        var provider = CreateProvider(settings =>
        {
            settings.AutomaticRefresh = true;
            settings.RefreshIntervalSeconds = 300;
            settings.RefreshTimeoutSeconds = 60;
        });
        _client.HoldListResponses();

        // When
        var refresh = provider.RefreshAsync(TestContext.Current.CancellationToken);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(
            () => refresh.IsCompleted,
            beforeEachCheck: () => _timeProvider.Advance(TimeSpan.FromSeconds(5)));
        var exception = await Record.ExceptionAsync(() => refresh);

        // Then
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.Null(await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GivenARefreshThatOutlastsTheOnDemandWait_WhenStoriesAreRequested_ThenTheRequestFailsAndTheRefreshCompletesInTheBackground()
    {
        // Given
        var provider = CreateProvider(settings =>
        {
            settings.OnDemandWaitSeconds = 10;
            settings.RefreshTimeoutSeconds = 60;
        });
        _client.HoldListResponses();

        // When
        var request = provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(
            () => request.IsCompleted,
            beforeEachCheck: () => _timeProvider.Advance(TimeSpan.FromSeconds(1)));
        var exception = await Record.ExceptionAsync(() => request);
        _client.ReleaseListResponses();

        // Then
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.True(_timeProvider.GetUtcNow() - _start < TimeSpan.FromSeconds(60));
        await Eventually.HoldsAsync(async () =>
            await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken) is not null);
        Assert.Equal(1, _client.ListRequests);
    }

    [Fact]
    public async Task GivenTheRefreshLockIsLost_WhenARefreshIsRunning_ThenTheRefreshIsAbandoned()
    {
        // Given
        using var refreshLock = new LosableRefreshLock();
        var provider = CreateProvider(
            settings =>
            {
                settings.AutomaticRefresh = true;
                settings.RefreshIntervalSeconds = 300;
            },
            refreshLock: refreshLock);
        _client.HoldListResponses();
        var refresh = provider.RefreshAsync(TestContext.Current.CancellationToken);
        await _client.ListRequested.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        // When
        await refreshLock.LoseAsync();
        var exception = await Record.ExceptionAsync(() => refresh);

        // Then
        Assert.IsType<StoryDataUnavailableException>(exception);
        Assert.Null(await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, refreshLock.Releases);
    }

    private async Task<StorySnapshot> PublishAsync(TimeSpan age)
    {
        var snapshot = TestStories.Snapshot(_timeProvider.GetUtcNow() - age, 100, 200);
        await _cache.SetSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        return snapshot;
    }

    private StoryProvider CreateProvider(
        Action<StoryCacheOptions>? configure = null,
        IDistributedRefreshLock? refreshLock = null,
        TimeProvider? timeProvider = null,
        FakeHackerNewsClient? client = null,
        Action<DistributedCacheOptions>? configureLock = null)
    {
        var settings = new StoryCacheOptions
        {
            AutomaticRefresh = false,
            CacheTtlSeconds = 60,
            MaxStalenessSeconds = 600,
            MaxConcurrentRequests = 2,
            FailureCooldownSeconds = 30
        };
        configure?.Invoke(settings);

        var lockSettings = new DistributedCacheOptions { LockWaitSeconds = 10, LockPollMilliseconds = 20 };
        configureLock?.Invoke(lockSettings);

        var time = timeProvider ?? _timeProvider;
        var options = Options.Create(settings);
        var builder = new StorySnapshotBuilder(
            client ?? _client,
            options,
            time,
            NullLogger<StorySnapshotBuilder>.Instance);

        return new StoryProvider(
            builder,
            _cache,
            refreshLock ?? new InMemoryRefreshLock(),
            options,
            Options.Create(lockSettings),
            time,
            _lifetime,
            NullLogger<StoryProvider>.Instance);
    }

    /// <summary>A refresh lock whose lease the test can mark as lost.</summary>
    private sealed class LosableRefreshLock : IDistributedRefreshLock, IDisposable
    {
        private readonly CancellationTokenSource _lost = new();
        private int _releases;

        public int Releases => Volatile.Read(ref _releases);

        public Task<IRefreshLease?> TryAcquireAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IRefreshLease?>(new Lease(this));

        public Task LoseAsync() => _lost.CancelAsync();

        public void Dispose() => _lost.Dispose();

        private sealed class Lease(LosableRefreshLock owner) : IRefreshLease
        {
            public CancellationToken LeaseLost => owner._lost.Token;

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._releases);
                return ValueTask.CompletedTask;
            }
        }
    }
}
