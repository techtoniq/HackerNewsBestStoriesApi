using System.Text.Json;
using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>
/// Shares the snapshot between instances through <see cref="IDistributedCache"/> (Redis in
/// production), with a short-lived local copy so that each request does not have to read and
/// deserialize the full snapshot from Redis.
/// </summary>
/// <remarks>
/// <para>
/// The last snapshot this instance read or wrote is kept regardless of
/// <see cref="DistributedCacheOptions.LocalCacheMilliseconds"/>. When Redis fails or takes longer
/// than <see cref="DistributedCacheOptions.ReadTimeout"/>, that snapshot is served instead (the
/// provider still applies the maximum staleness), and Redis is not tried again for
/// <see cref="DistributedCacheOptions.ReadFailureBackoff"/>. A Redis outage therefore costs each
/// instance one slow read, not one per request, and instances that hold a snapshot keep serving it.
/// </para>
/// <para>
/// A shared payload that cannot be deserialized or fails <see cref="StorySnapshot.IsValid"/> is
/// treated as missing, so the next refresh replaces it rather than every read failing until
/// someone deletes the key.
/// </para>
/// </remarks>
public sealed partial class RedisStoryCache(
    IDistributedCache distributedCache,
    IOptions<DistributedCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisStoryCache> logger) : IStoryCache
{
    /// <summary>
    /// Versioned so that a deployment that changes the snapshot format does not read snapshots
    /// written by the previous version during a rolling update. Bump the suffix on any breaking
    /// change to <see cref="StorySnapshot"/> or <see cref="Story"/>.
    /// </summary>
    internal const string SnapshotKey = "best-stories:snapshot:v1";

    private const long NoFailure = long.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LogThrottle _fallbackLog = new(timeProvider);
    private readonly LogThrottle _rejectedLog = new(timeProvider);
    private LocalCopy? _localCopy;
    private long _lastReadFailureTimestamp = NoFailure;

    public async Task<StorySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var localCopy = Volatile.Read(ref _localCopy);
        if (localCopy is not null &&
            timeProvider.GetElapsedTime(localCopy.ReadAtTimestamp) < settings.LocalCacheDuration)
        {
            return localCopy.Snapshot;
        }

        if (localCopy is not null && IsBackingOff(settings))
        {
            StoryTelemetry.SharedCacheFallbacks.Add(1);
            return localCopy.Snapshot;
        }

        byte[]? payload;
        try
        {
            // The Redis command keeps running after a timeout; only this read stops waiting for it.
            payload = await distributedCache
                .GetAsync(SnapshotKey, cancellationToken)
                .WaitAsync(settings.ReadTimeout, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (localCopy is not null)
        {
            Interlocked.Exchange(ref _lastReadFailureTimestamp, timeProvider.GetTimestamp());
            StoryTelemetry.SharedCacheFallbacks.Add(1);
            if (_fallbackLog.ShouldLog(out var suppressed))
            {
                LogServingLocalCopy(logger, exception, localCopy.Snapshot.PublishedAt, suppressed);
            }

            return localCopy.Snapshot;
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _lastReadFailureTimestamp, timeProvider.GetTimestamp());
            throw;
        }

        Interlocked.Exchange(ref _lastReadFailureTimestamp, NoFailure);
        if (payload is null)
        {
            return null;
        }

        var snapshot = Deserialize(payload);
        if (snapshot is not null)
        {
            Remember(snapshot);
        }

        return snapshot;
    }

    public async Task SetSnapshotAsync(StorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        await distributedCache.SetAsync(SnapshotKey, payload, new DistributedCacheEntryOptions(), cancellationToken);
        Remember(snapshot);
    }

    private StorySnapshot? Deserialize(byte[] payload)
    {
        string? problem;
        try
        {
            var snapshot = JsonSerializer.Deserialize<StorySnapshot>(payload, JsonOptions);
            if (StorySnapshot.IsValid(snapshot, out problem))
            {
                return snapshot;
            }
        }
        catch (JsonException exception)
        {
            problem = $"it is not valid JSON ({exception.Message})";
        }

        StoryTelemetry.RejectedSnapshots.Add(1);
        if (_rejectedLog.ShouldLog(out var suppressed))
        {
            LogSnapshotRejected(logger, problem, suppressed);
        }

        return null;
    }

    private bool IsBackingOff(DistributedCacheOptions settings)
    {
        var failedAt = Interlocked.Read(ref _lastReadFailureTimestamp);
        return failedAt != NoFailure && timeProvider.GetElapsedTime(failedAt) < settings.ReadFailureBackoff;
    }

    private void Remember(StorySnapshot snapshot) =>
        Volatile.Write(ref _localCopy, new LocalCopy(snapshot, timeProvider.GetTimestamp()));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read the shared story snapshot; serving this instance's copy published at {PublishedAt}. {SuppressedCount} similar failures were not logged since the last one.")]
    private static partial void LogServingLocalCopy(
        ILogger logger,
        Exception exception,
        DateTimeOffset publishedAt,
        long suppressedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring the shared story snapshot because {Problem}; the next refresh replaces it. {SuppressedCount} similar rejections were not logged since the last one.")]
    private static partial void LogSnapshotRejected(ILogger logger, string? problem, long suppressedCount);

    private sealed record LocalCopy(StorySnapshot Snapshot, long ReadAtTimestamp);
}
