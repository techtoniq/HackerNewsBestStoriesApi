using System.Diagnostics;
using BestStories.Api.Models;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Services;

public sealed class StoryProvider(
    IHackerNewsClient hackerNewsClient,
    IStoryCache storyCache,
    IDistributedRefreshLock refreshLock,
    IOptions<StoryCacheOptions> options,
    IOptions<DistributedCacheOptions> distributedCacheOptions,
    IHostApplicationLifetime applicationLifetime,
    ILogger<StoryProvider> logger) : IStoryProvider
{
    private readonly object _refreshLock = new();
    private Task? _refreshTask;

    public async Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetBestStoriesCoreAsync(count, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoryDataUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read the story cache.");
            throw new StoryDataUnavailableException("The story data is unavailable.", exception);
        }
    }

    private async Task<IReadOnlyList<StoryResponse>> GetBestStoriesCoreAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (settings.AutomaticRefresh)
        {
            var snapshot = await storyCache.GetSnapshotAsync(cancellationToken);
            StoryTelemetry.CacheReads.Add(1,
                new KeyValuePair<string, object?>("result", snapshot is null ? "miss" : "hit"),
                new KeyValuePair<string, object?>("mode", "automatic"));
            return snapshot?.Stories.Take(count).ToArray()
                ?? throw new StoryDataUnavailableException("The story cache has not been initialized.");
        }

        var maximumAge = TimeSpan.FromSeconds(settings.CacheTtlSeconds);
        var snapshotBeforeRefresh = await storyCache.GetSnapshotAsync(cancellationToken);
        var isFresh = IsFresh(snapshotBeforeRefresh, maximumAge);
        StoryTelemetry.CacheReads.Add(1,
            new KeyValuePair<string, object?>("result", isFresh ? "hit" : "miss"),
            new KeyValuePair<string, object?>("mode", "on_demand"));
        if (!isFresh)
        {
            await GetOrStartRefreshTask("on_demand", maximumAge).WaitAsync(cancellationToken);
        }

        var refreshedSnapshot = await storyCache.GetSnapshotAsync(cancellationToken);
        return refreshedSnapshot?.Stories.Take(count).ToArray()
            ?? throw new StoryDataUnavailableException("The story cache is not available.");
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GetOrStartRefreshTask("scheduled").WaitAsync(cancellationToken);
    }

    private static bool IsFresh(StoryCacheSnapshot? snapshot, TimeSpan maximumAge) =>
        snapshot is not null && DateTimeOffset.UtcNow - snapshot.PublishedAt < maximumAge;

    private Task GetOrStartRefreshTask(
        string trigger,
        TimeSpan? maximumAge = null)
    {
        lock (_refreshLock)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                StoryTelemetry.CoalescedRefreshes.Add(1, new KeyValuePair<string, object?>("trigger", trigger));
                return _refreshTask;
            }

            _refreshTask = RefreshWithDistributedLockAsync(
                applicationLifetime.ApplicationStopping,
                trigger,
                maximumAge);
            return _refreshTask;
        }
    }

    private async Task RefreshWithDistributedLockAsync(
        CancellationToken cancellationToken,
        string trigger,
        TimeSpan? maximumAge)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(distributedCacheOptions.Value.LockWaitSeconds);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lease = await refreshLock.TryAcquireAsync(cancellationToken);
                if (lease is not null)
                {
                    await using (lease)
                    {
                        if (maximumAge is not null &&
                            IsFresh(await storyCache.GetSnapshotAsync(cancellationToken), maximumAge.Value))
                        {
                            StoryTelemetry.CoalescedRefreshes.Add(
                                1,
                                new KeyValuePair<string, object?>("trigger", "distributed_cache_hit"));
                            return;
                        }

                        await RefreshCoreAsync(cancellationToken, trigger);
                        return;
                    }
                }

                if (maximumAge is null)
                {
                    logger.LogInformation("Skipping scheduled refresh because another instance holds the refresh lock.");
                    return;
                }

                if (IsFresh(await storyCache.GetSnapshotAsync(cancellationToken), maximumAge.Value))
                {
                    StoryTelemetry.CoalescedRefreshes.Add(
                        1,
                        new KeyValuePair<string, object?>("trigger", "distributed_cache_hit"));
                    return;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(distributedCacheOptions.Value.LockPollMilliseconds),
                    cancellationToken);
            }
            while (DateTimeOffset.UtcNow < deadline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoryDataUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The shared story cache or refresh lock is unavailable.");
            throw new StoryDataUnavailableException("The story data could not be refreshed.", exception);
        }

        throw new StoryDataUnavailableException("The shared story cache refresh is still in progress.");
    }

    private async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        string trigger)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var activity = StoryTelemetry.ActivitySource.StartActivity("stories.cache.refresh");
        activity?.SetTag("refresh.trigger", trigger);

        try
        {
            var ids = await hackerNewsClient.GetBestStoryIdsAsync(cancellationToken);
            using var concurrencyLimiter = new SemaphoreSlim(options.Value.MaxConcurrentRequests);

            var fetches = ids.Select(async id =>
            {
                await concurrencyLimiter.WaitAsync(cancellationToken);
                try
                {
                    return await hackerNewsClient.GetStoryAsync(id, cancellationToken);
                }
                finally
                {
                    concurrencyLimiter.Release();
                }
            });

            var stories = (await Task.WhenAll(fetches)).OfType<StoryResponse>().ToArray();
            await storyCache.SetSnapshotAsync(stories, cancellationToken);
            activity?.SetTag("stories.count", stories.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);
            StoryTelemetry.Refreshes.Add(1,
                new KeyValuePair<string, object?>("result", "success"),
                new KeyValuePair<string, object?>("trigger", trigger));
            StoryTelemetry.StoriesPerRefresh.Record(stories.Length);
            logger.LogInformation(
                "Refreshed the best-stories cache with {StoryCount} stories. Trigger: {RefreshTrigger}.",
                stories.Length,
                trigger);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Refresh cancelled during application shutdown.");
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            StoryTelemetry.Refreshes.Add(1,
                new KeyValuePair<string, object?>("result", "failure"),
                new KeyValuePair<string, object?>("trigger", trigger));
            logger.LogError(
                exception,
                "Could not refresh the best-stories cache; retaining the previous snapshot. Trigger: {RefreshTrigger}.",
                trigger);
            throw new StoryDataUnavailableException("The story data could not be refreshed.", exception);
        }
        finally
        {
            StoryTelemetry.RefreshDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("trigger", trigger));
        }
    }
}
