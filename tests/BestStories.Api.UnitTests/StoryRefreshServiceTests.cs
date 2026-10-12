using System.Diagnostics;
using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BestStories.Api.UnitTests;

public sealed class StoryRefreshServiceTests
{
    [Fact]
    public async Task GivenAutomaticRefreshEnabled_WhenTheServiceStarts_ThenAnInitialRefreshBegins()
    {
        // Given
        var client = new BlockingHackerNewsClient();
        var cache = new StoryCache();
        var (_, service) = CreateServices(client, cache, automaticRefresh: true);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);
        await client.BestStoryListRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        client.AllowBestStoryListResponse();
        await WaitUntilAsync(async () =>
            await cache.GetSnapshotAsync(TestContext.Current.CancellationToken) is not null);

        // Then
        Assert.Equal(1, client.BestStoryListRequests);
        Assert.Equal(1, client.StoryRequests);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenOnDemandRefresh_WhenTheServiceStarts_ThenNoUpstreamRequestBegins()
    {
        // Given
        var client = new BlockingHackerNewsClient();
        var cache = new StoryCache();
        var (_, service) = CreateServices(client, cache);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(0, client.BestStoryListRequests);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenConcurrentOnDemandCacheMisses_WhenStoriesAreRequested_ThenOneRefreshServesBothRequests()
    {
        // Given
        var client = new BlockingHackerNewsClient();
        var cache = new StoryCache();
        var (provider, service) = CreateServices(client, cache);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);
        var firstRequest = provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        await client.BestStoryListRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var secondRequest = provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        client.AllowBestStoryListResponse();

        var results = await Task.WhenAll(firstRequest, secondRequest);

        // Then
        Assert.Equal(1, client.BestStoryListRequests);
        Assert.Equal(1, client.StoryRequests);
        Assert.All(results, stories => Assert.Single(stories));
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.True(DateTimeOffset.UtcNow - snapshot.PublishedAt < TimeSpan.FromSeconds(60));

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenTwoInstancesAndOneSharedRefreshLock_WhenBothCachesMiss_ThenOnlyOneInstanceFetchesUpstream()
    {
        // Given
        var sharedCache = new StoryCache();
        var sharedLock = new InMemoryRefreshLock();
        var firstClient = new BlockingHackerNewsClient();
        var secondClient = new BlockingHackerNewsClient();
        var (firstProvider, firstService) = CreateServices(firstClient, sharedCache, refreshLock: sharedLock);
        var (secondProvider, secondService) = CreateServices(secondClient, sharedCache, refreshLock: sharedLock);

        // When
        await firstService.StartAsync(TestContext.Current.CancellationToken);
        await secondService.StartAsync(TestContext.Current.CancellationToken);
        var firstRequest = firstProvider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        await firstClient.BestStoryListRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var secondRequest = secondProvider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        firstClient.AllowBestStoryListResponse();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        // Then
        Assert.Equal(1, firstClient.BestStoryListRequests);
        Assert.Equal(0, secondClient.BestStoryListRequests);
        Assert.All(results, stories => Assert.Single(stories));

        await firstService.StopAsync(TestContext.Current.CancellationToken);
        await secondService.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenAFreshOnDemandCache_WhenStoriesAreRequestedAgain_ThenNoRefreshIsStarted()
    {
        // Given
        var client = new BlockingHackerNewsClient();
        var cache = new StoryCache();
        var (provider, service) = CreateServices(client, cache);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);
        var initialRequest = provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        await client.BestStoryListRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        client.AllowBestStoryListResponse();
        await initialRequest;

        await provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, client.BestStoryListRequests);
        Assert.Equal(1, client.StoryRequests);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenAnExpiredOnDemandCache_WhenStoriesAreRequested_ThenTheCacheIsRefreshed()
    {
        // Given
        var client = new BlockingHackerNewsClient();
        var cache = new StoryCache();
        var (provider, service) = CreateServices(client, cache, cacheTtlSeconds: 1);

        await service.StartAsync(TestContext.Current.CancellationToken);
        var initialRequest = provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);
        await client.BestStoryListRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        client.AllowBestStoryListResponse();
        await initialRequest;
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);

        // When
        await provider.GetBestStoriesAsync(1, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(2, client.BestStoryListRequests);
        Assert.Equal(2, client.StoryRequests);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenMoreStoriesThanTheConcurrencyLimit_WhenTheCacheIsRefreshed_ThenItemFetchesStayBounded()
    {
        // Given
        const int concurrencyLimit = 2;
        var client = new ConcurrencyTrackingHackerNewsClient(Enumerable.Range(1, 8).ToArray());
        var cache = new StoryCache();
        var (provider, _) = CreateServices(
            client,
            cache,
            maxConcurrentRequests: concurrencyLimit);

        // When
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(8, client.StoryRequests);
        Assert.Equal(concurrencyLimit, client.MaximumConcurrentStoryRequests);
    }

    [Fact]
    public async Task GivenAnExistingSnapshotAndAnUpstreamFailure_WhenTheCacheIsRefreshed_ThenTheSnapshotIsRetained()
    {
        // Given
        var cache = new StoryCache();
        var existingStory = new StoryResponse(
            "Previously cached story",
            "https://example.com/previous",
            "author",
            DateTimeOffset.UnixEpoch,
            100,
            10);
        await cache.SetSnapshotAsync([existingStory], TestContext.Current.CancellationToken);
        var client = new FailingHackerNewsClient();
        var (provider, _) = CreateServices(client, cache);

        // When
        var exception = await Assert.ThrowsAsync<StoryDataUnavailableException>(
            () => provider.RefreshAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        var snapshot = await cache.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal([existingStory], snapshot.Stories);
    }

    private static (StoryProvider Provider, StoryRefreshService RefreshService) CreateServices(
        IHackerNewsClient client,
        StoryCache cache,
        int cacheTtlSeconds = 60,
        bool automaticRefresh = false,
        IDistributedRefreshLock? refreshLock = null,
        int maxConcurrentRequests = 2)
    {
        var options = Options.Create(new StoryCacheOptions
        {
            AutomaticRefresh = automaticRefresh,
            CacheTtlSeconds = cacheTtlSeconds,
            MaxConcurrentRequests = maxConcurrentRequests
        });
        var lifetime = new TestHostApplicationLifetime();
        refreshLock ??= new InMemoryRefreshLock();
        var provider = new StoryProvider(
            client,
            cache,
            refreshLock,
            options,
            Options.Create(new DistributedCacheOptions()),
            lifetime,
            NullLogger<StoryProvider>.Instance);
        var refreshService = new StoryRefreshService(
            provider,
            options);
        return (provider, refreshService);
    }

    private sealed class ConcurrencyTrackingHackerNewsClient(IReadOnlyList<int> storyIds) : IHackerNewsClient
    {
        private int _activeStoryRequests;
        private int _maximumConcurrentStoryRequests;
        private int _storyRequests;

        public int StoryRequests => Volatile.Read(ref _storyRequests);

        public int MaximumConcurrentStoryRequests => Volatile.Read(ref _maximumConcurrentStoryRequests);

        public Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(storyIds);

        public async Task<StoryResponse?> GetStoryAsync(int id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _storyRequests);
            var activeRequests = Interlocked.Increment(ref _activeStoryRequests);
            UpdateMaximumConcurrentRequests(activeRequests);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                return new StoryResponse(
                    $"Story {id}",
                    $"https://example.com/{id}",
                    "author",
                    DateTimeOffset.UnixEpoch,
                    id,
                    0);
            }
            finally
            {
                Interlocked.Decrement(ref _activeStoryRequests);
            }
        }

        private void UpdateMaximumConcurrentRequests(int activeRequests)
        {
            int previousMaximum;
            do
            {
                previousMaximum = Volatile.Read(ref _maximumConcurrentStoryRequests);
                if (activeRequests <= previousMaximum)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(
                       ref _maximumConcurrentStoryRequests,
                       activeRequests,
                       previousMaximum) != previousMaximum);
        }
    }

    private sealed class FailingHackerNewsClient : IHackerNewsClient
    {
        public Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("Upstream failure."));

        public Task<StoryResponse?> GetStoryAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The expected condition was not reached.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class BlockingHackerNewsClient : IHackerNewsClient
    {
        private readonly TaskCompletionSource _requestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _responseAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bestStoryListRequests;
        private int _storyRequests;

        public TaskCompletionSource BestStoryListRequestStarted => _requestStarted;

        public int BestStoryListRequests => Volatile.Read(ref _bestStoryListRequests);

        public int StoryRequests => Volatile.Read(ref _storyRequests);

        public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bestStoryListRequests);
            _requestStarted.TrySetResult();
            await _responseAllowed.Task.WaitAsync(cancellationToken);
            return [42];
        }

        public Task<StoryResponse?> GetStoryAsync(int id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _storyRequests);
            return Task.FromResult<StoryResponse?>(
                new StoryResponse(
                    "A story",
                    "https://example.com/story",
                    "author",
                    DateTimeOffset.UtcNow,
                    10,
                    2));
        }

        public void AllowBestStoryListResponse() => _responseAllowed.TrySetResult();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }
}
