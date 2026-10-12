using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

public sealed class StoryRefreshServiceTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly InMemoryStoryCache _cache = new();
    private readonly FakeHackerNewsClient _client = new();
    private readonly TestHostApplicationLifetime _lifetime = new();

    public void Dispose() => _lifetime.Dispose();

    [Fact]
    public async Task GivenAutomaticRefresh_WhenTheServiceStarts_ThenTheInitialSnapshotIsLoaded()
    {
        // Given
        using var service = CreateService(automaticRefresh: true);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Then
        await Eventually.HoldsAsync(async () =>
            await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken) is not null);
        Assert.Equal(1, _client.ListRequests);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenOnDemandRefresh_WhenTheServiceStarts_ThenItExitsWithoutCallingUpstream()
    {
        // Given
        using var service = CreateService(automaticRefresh: false);

        // When
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(0, _client.ListRequests);
    }

    [Fact]
    public async Task GivenTheInitialLoadFails_WhenTheServiceStarts_ThenItRetriesUntilTheLoadSucceeds()
    {
        // Given
        _client.ListFailure = new HttpRequestException("Upstream failure.");
        using var service = CreateService(automaticRefresh: true);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(() => _client.ListRequests >= 1);

        // When
        _client.ListFailure = null;

        // Then
        await Eventually.HoldsAsync(
            async () => await _cache.GetSnapshotAsync(TestContext.Current.CancellationToken) is not null,
            beforeEachCheck: () => _timeProvider.Advance(TimeSpan.FromSeconds(1)));
        Assert.True(_client.ListRequests >= 2);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenALoadedSnapshot_WhenTheRefreshIntervalElapses_ThenTheSnapshotIsRefreshed()
    {
        // Given
        using var service = CreateService(automaticRefresh: true);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.HoldsAsync(() => _client.ListRequests == 1);

        // When / Then
        await Eventually.HoldsAsync(
            () => _client.ListRequests >= 2,
            beforeEachCheck: () => _timeProvider.Advance(TimeSpan.FromSeconds(30)));
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private StoryRefreshService CreateService(bool automaticRefresh)
    {
        var options = Options.Create(new StoryCacheOptions
        {
            AutomaticRefresh = automaticRefresh,
            RefreshIntervalSeconds = 300,
            MaxStalenessSeconds = 1800,
            InitialLoadMaxRetryDelaySeconds = 4
        });
        var builder = new StorySnapshotBuilder(
            _client,
            options,
            _timeProvider,
            NullLogger<StorySnapshotBuilder>.Instance);
        var provider = new StoryProvider(
            builder,
            _cache,
            new InMemoryRefreshLock(),
            options,
            Options.Create(new DistributedCacheOptions()),
            _timeProvider,
            _lifetime,
            NullLogger<StoryProvider>.Instance);

        return new StoryRefreshService(
            provider,
            options,
            _timeProvider,
            NullLogger<StoryRefreshService>.Instance);
    }
}
