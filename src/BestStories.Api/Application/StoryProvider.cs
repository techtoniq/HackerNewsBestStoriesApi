using System.Diagnostics;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Application;

/// <summary>
/// Serves the best-stories snapshot and decides when it must be refreshed.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is <em>fresh</em> for the refresh interval (automatic mode) or cache TTL (on-demand
/// mode) and <em>acceptable</em> up to the maximum staleness. Fresh snapshots are served directly.
/// Stale but acceptable snapshots are still served; in on-demand mode they also start a refresh
/// in the background (stale-while-revalidate). With no acceptable snapshot, on-demand requests
/// wait for a refresh and automatic-mode requests fail with
/// <see cref="StoryDataUnavailableException"/>.
/// </para>
/// <para>
/// Refreshes are coalesced within the instance (one in-flight refresh at a time) and across
/// instances (through <see cref="IDistributedRefreshLock"/> and a freshness re-check once the
/// lock is held). After a failed refresh, on-demand requests that need one fail fast for
/// <see cref="StoryCacheOptions.FailureCooldown"/> rather than retrying the upstream on every request.
/// </para>
/// <para>
/// Every refresh is bounded by <see cref="StoryCacheOptions.RefreshTimeout"/> and abandoned if the
/// refresh lock is lost, and an on-demand request waits at most
/// <see cref="StoryCacheOptions.OnDemandWait"/> for it; the refresh carries on in the background
/// after a waiting request gives up.
/// </para>
/// </remarks>
public sealed partial class StoryProvider(
    StorySnapshotBuilder snapshotBuilder,
    IStoryCache storyCache,
    IDistributedRefreshLock refreshLock,
    IOptions<StoryCacheOptions> options,
    IOptions<DistributedCacheOptions> distributedCacheOptions,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<StoryProvider> logger) : IStoryProvider
{
    private const long NoFailure = long.MinValue;

    /// <summary>
    /// Scheduled refreshes skip when another instance published within this fraction of the
    /// refresh interval, so N instances do not each refresh once per interval.
    /// </summary>
    private const double ScheduledFreshnessFraction = 0.9;

    private readonly Lock _refreshGate = new();
    private Task? _refreshTask;
    private long _lastFailureTimestamp = NoFailure;

    private enum RefreshTrigger
    {
        Scheduled,
        OnDemand,
        Background
    }

    public async Task<StorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetSnapshotCoreAsync(cancellationToken);
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
            LogSnapshotReadFailed(logger, exception);
            throw new StoryDataUnavailableException("The story data is unavailable.", exception);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        GetOrStartRefresh(RefreshTrigger.Scheduled).WaitAsync(cancellationToken);

    private async Task<StorySnapshot> GetSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        var age = snapshot?.AgeAt(timeProvider.GetUtcNow());

        if (snapshot is not null && age < settings.FreshFor)
        {
            RecordRead("hit");
            return snapshot;
        }

        if (snapshot is not null && age <= settings.MaxStaleness)
        {
            RecordRead("stale");
            if (!settings.AutomaticRefresh && !IsCoolingDown())
            {
                StartBackgroundRefresh();
            }

            return snapshot;
        }

        RecordRead("miss");
        if (settings.AutomaticRefresh)
        {
            throw new StoryDataUnavailableException(snapshot is null
                ? "The story cache has not been initialized."
                : "The story cache is older than the maximum staleness.");
        }

        if (IsCoolingDown())
        {
            StoryTelemetry.CooldownRejections.Add(1);
            throw new StoryDataUnavailableException("A recent refresh failed; waiting before trying again.");
        }

        try
        {
            await GetOrStartRefresh(RefreshTrigger.OnDemand)
                .WaitAsync(settings.OnDemandWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            StoryTelemetry.OnDemandWaitTimeouts.Add(1);
            LogOnDemandWaitTimedOut(logger, settings.OnDemandWait.TotalSeconds);
            throw new StoryDataUnavailableException(
                "The story refresh did not finish in time; it continues in the background.");
        }

        return await ReadSnapshotAsync(cancellationToken)
            ?? throw new StoryDataUnavailableException("The story cache is not available.");
    }

    private void StartBackgroundRefresh() => _ = GetOrStartRefresh(RefreshTrigger.Background);

    private Task GetOrStartRefresh(RefreshTrigger trigger)
    {
        lock (_refreshGate)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                RecordCoalesced(TriggerName(trigger));
                return _refreshTask;
            }

            var stoppingToken = applicationLifetime.ApplicationStopping;

            // The refresh is shared by every waiting request, so it is bound to application
            // shutdown rather than to any one request, and runs outside the lock.
            _refreshTask = Task.Run(() => RefreshWithLockAsync(trigger, stoppingToken), CancellationToken.None);

            // The refresh logs its own failures, and callers may stop waiting before it finishes
            // (background refreshes and timed-out on-demand waits). Observing the task here stops
            // an unobserved exception from surfacing on the finalizer thread.
            _ = _refreshTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return _refreshTask;
        }
    }

    private async Task RefreshWithLockAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var lockSettings = distributedCacheOptions.Value;
        var freshFor = trigger == RefreshTrigger.Scheduled
            ? settings.RefreshInterval * ScheduledFreshnessFraction
            : settings.FreshFor;
        var deadline = timeProvider.GetUtcNow() + lockSettings.LockWait;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lease = await refreshLock.TryAcquireAsync(cancellationToken);
                if (lease is not null)
                {
                    await using (lease)
                    {
                        if (await IsFreshAsync(freshFor, cancellationToken))
                        {
                            RecordCoalesced("shared_cache");
                            ClearFailure();
                            return;
                        }

                        await RefreshUnderLeaseAsync(trigger, lease.LeaseLost, cancellationToken);
                        return;
                    }
                }

                if (trigger == RefreshTrigger.Scheduled)
                {
                    LogScheduledRefreshSkipped(logger);
                    return;
                }

                if (await IsFreshAsync(freshFor, cancellationToken))
                {
                    RecordCoalesced("shared_cache");
                    return;
                }

                if (timeProvider.GetUtcNow() >= deadline)
                {
                    throw new StoryDataUnavailableException(
                        "Another instance is still refreshing the shared story cache.");
                }

                await Task.Delay(lockSettings.LockPoll, timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoryDataUnavailableException)
        {
            RecordFailure();
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure();
            LogSharedCacheFailed(logger, exception);
            throw new StoryDataUnavailableException("The story data could not be refreshed.", exception);
        }
    }

    /// <summary>
    /// Runs the refresh until it completes, the refresh timeout passes, the lease is lost or the
    /// application stops, whichever comes first.
    /// </summary>
    private async Task RefreshUnderLeaseAsync(
        RefreshTrigger trigger,
        CancellationToken leaseLost,
        CancellationToken stoppingToken)
    {
        var timeout = options.Value.RefreshTimeout;
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, leaseLost, deadline.Token);

        try
        {
            await RefreshCoreAsync(trigger, refresh.Token, stoppingToken);
        }
        catch (StoryDataUnavailableException) when (
            deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            StoryTelemetry.RefreshTimeouts.Add(1, StoryTelemetry.Tag("trigger", TriggerName(trigger)));
            LogRefreshTimedOut(logger, timeout.TotalSeconds, TriggerName(trigger));
            throw;
        }
    }

    private async Task RefreshCoreAsync(
        RefreshTrigger trigger,
        CancellationToken cancellationToken,
        CancellationToken stoppingToken)
    {
        var triggerName = TriggerName(trigger);
        var startedAt = timeProvider.GetTimestamp();
        using var activity = StoryTelemetry.ActivitySource.StartActivity("stories.cache.refresh");
        activity?.SetTag("refresh.trigger", triggerName);

        try
        {
            var snapshot = await snapshotBuilder.BuildAsync(cancellationToken);
            await storyCache.SetSnapshotAsync(snapshot, cancellationToken);
            StoryTelemetry.ObserveSnapshot(snapshot.PublishedAt);
            ClearFailure();

            activity?.SetTag("stories.count", snapshot.Stories.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            StoryTelemetry.Refreshes.Add(
                1,
                StoryTelemetry.Tag("result", "success"),
                StoryTelemetry.Tag("trigger", triggerName));
            StoryTelemetry.StoriesPerRefresh.Record(snapshot.Stories.Count);
            LogRefreshed(logger, snapshot.Stories.Count, triggerName);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Refresh cancelled during application shutdown.");
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            StoryTelemetry.Refreshes.Add(
                1,
                StoryTelemetry.Tag("result", "failure"),
                StoryTelemetry.Tag("trigger", triggerName));
            LogRefreshFailed(logger, exception, triggerName);
            throw new StoryDataUnavailableException("The story data could not be refreshed.", exception);
        }
        finally
        {
            StoryTelemetry.RefreshDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                StoryTelemetry.Tag("trigger", triggerName));
        }
    }

    /// <summary>Reads the shared snapshot and records its publication time for the age gauge.</summary>
    private async Task<StorySnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await storyCache.GetSnapshotAsync(cancellationToken);
        if (snapshot is not null)
        {
            StoryTelemetry.ObserveSnapshot(snapshot.PublishedAt);
        }

        return snapshot;
    }

    private async Task<bool> IsFreshAsync(TimeSpan freshFor, CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        return snapshot is not null && snapshot.AgeAt(timeProvider.GetUtcNow()) < freshFor;
    }

    private bool IsCoolingDown()
    {
        var failedAt = Interlocked.Read(ref _lastFailureTimestamp);
        return failedAt != NoFailure &&
            timeProvider.GetElapsedTime(failedAt) < options.Value.FailureCooldown;
    }

    private void RecordFailure() =>
        Interlocked.Exchange(ref _lastFailureTimestamp, timeProvider.GetTimestamp());

    private void ClearFailure() =>
        Interlocked.Exchange(ref _lastFailureTimestamp, NoFailure);

    private static void RecordRead(string result) =>
        StoryTelemetry.CacheReads.Add(1, StoryTelemetry.Tag("result", result));

    private static void RecordCoalesced(string reason) =>
        StoryTelemetry.CoalescedRefreshes.Add(1, StoryTelemetry.Tag("reason", reason));

    private static string TriggerName(RefreshTrigger trigger) => trigger switch
    {
        RefreshTrigger.Scheduled => "scheduled",
        RefreshTrigger.OnDemand => "on_demand",
        RefreshTrigger.Background => "background",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the story cache.")]
    private static partial void LogSnapshotReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The shared story cache or refresh lock is unavailable.")]
    private static partial void LogSharedCacheFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipping the scheduled refresh because another instance holds the refresh lock.")]
    private static partial void LogScheduledRefreshSkipped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A request stopped waiting for the story refresh after {WaitSeconds} seconds; the refresh continues.")]
    private static partial void LogOnDemandWaitTimedOut(ILogger logger, double waitSeconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The best-stories refresh did not finish within {TimeoutSeconds} seconds and was abandoned. Trigger: {RefreshTrigger}.")]
    private static partial void LogRefreshTimedOut(ILogger logger, double timeoutSeconds, string refreshTrigger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Refreshed the best-stories cache with {StoryCount} stories. Trigger: {RefreshTrigger}.")]
    private static partial void LogRefreshed(ILogger logger, int storyCount, string refreshTrigger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not refresh the best-stories cache; keeping the previous snapshot. Trigger: {RefreshTrigger}.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, string refreshTrigger);
}
